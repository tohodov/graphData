using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";
    static readonly HashSet<string> ReservedDynamicTypeChildNames = new(StringComparer.OrdinalIgnoreCase) {
        "Definition",
        "Fields",
        "Slots"
    };

    readonly Graph graph;
    readonly GraphSearchService searchService;

    internal GraphService(
        Graph graph,
        GraphSearchService searchService
    ) {
        graph.EnsureOpen();
        this.graph = graph;
        this.searchService = searchService;
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNodeCore(graph, localId, path, type, attributes).ConfigureAwait(false);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> CreateNodeType(
        NodeLocalId localId,
        bool isAbstract = false,
        IEnumerable<NodeFieldDefinition>? fields = null,
        IEnumerable<NodeSlotDefinition>? slots = null) {
        var fieldArray = fields?.ToArray() ?? [];
        var slotArray = slots?.ToArray() ?? [];

        var validation = await ValidateNodeTypeDefinitionRequest(localId, fieldArray, slotArray).ConfigureAwait(false);
        if (validation.Status != ServiceResultStatus.Ok)
            return new ServiceResult<NodeTypeDefinition>(validation.Status, Error: validation.Error);

        var storage = graph.Storage;
        var existing = await storage.Get(graph.NodeTypes.GlobalId, localId).ConfigureAwait(false);
        if (existing is not null)
            return ServiceResult<NodeTypeDefinition>.Conflict($"Node type '{localId}' already exists.");

        NodeBacking? created = null;
        try {
            created = await storage.Create(localId, graph.NodeTypes.GlobalId).ConfigureAwait(false);
            var type = new NodeType(created);
            var definition = CreateNodeTypeDefinition(type, isAbstract, fieldArray, slotArray);
            await DynamicNodeTypeDefinitionStorage.WriteAsync(storage, definition).ConfigureAwait(false);
            graph.RegisterNodeTypeDefinition(definition);
            return ServiceResult<NodeTypeDefinition>.Ok(definition);
        } catch (Exception ex) {
            if (created is not null)
                await storage.Delete(created).ConfigureAwait(false);
            return ServiceResult<NodeTypeDefinition>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType {
        var type = graph.GetRuntimeType<TNodeType>();
        if (type == null)
            return ServiceResult<Node>.NotFound();
        return await CreateNodeCore(graph, localId, path, type, attributes).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Node>> CreateNodeCore(Graph graph, NodeLocalId localId, NodeRef? parent, NodeType? type, IDictionary<string, string>? attributes) {
        parent = NormalizeParent(parent);
        var storage = graph.Storage;
        var node = await storage.Create(localId, parent, attributes);
        if (type is null)
            return new Node(node);

        var assign = await AssignNodeTypeAsync(node.GlobalId, type.GlobalId).ConfigureAwait(false);
        if (assign.Status != ServiceResultStatus.Ok) {
            await storage.Delete(node.GlobalId).ConfigureAwait(false);
            return ServiceResult<Node>.From(assign);
        }
        var reloaded = await storage.Get(node.GlobalId).ConfigureAwait(false); //TODO проверить что перезагрузка не нужна и удалить
        var result = new Node(reloaded ?? node);
        AttachInstanceOf(result, type);
        return result;
    }

    public async Task<ServiceResult<Node>> GetNode(NodeRef path) {
        var storage = graph.Storage;
        var result = await storage.Get(path);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult<Node>> GetNode(NodeRef path, NodeLocalId localId) {
        var storage = graph.Storage;
        var result = await storage.Get(path, localId);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult> UpdateNode(NodeRef path, IDictionary<string, string> attributes) {
        var storage = graph.Storage;
        await storage.Update(path, attributes);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteNode(NodeRef globalId) {
        var storage = graph.Storage;
        var node = await storage.Get(globalId);
        if (node == null)
            return ServiceResult.NotFound();
        if (node.GlobalId == storage.Root.GlobalId)
            return ServiceResult.BadRequest("Storage root cannot be deleted.");
        await storage.Delete(node);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ConnectNodesAsync(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        var storage = graph.Storage;
        await storage.Connect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }
    public async Task<ServiceResult> Disconnect(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        var storage = graph.Storage;
        await storage.Disconnect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId) where TNodeType : NodeType {
        var type = graph.GetRuntimeType<TNodeType>();
        if (type == null)
            return ServiceResult<Subgraph>.NotFound();
        return await AssignNodeTypeAsync(nodeId, type.GlobalId).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(NodeRef nodeId, NodeRef typeId) {
        var storage = graph.Storage;
        var type = await storage.Get(typeId).ConfigureAwait(false);
        if (type is null)
            return ServiceResult<Subgraph>.NotFound();
        var typeNode = await graph.AsNodeTypeAsync(type).ConfigureAwait(false);
        if (typeNode == null)
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not a node type.");

        var node = await storage.Get(nodeId).ConfigureAwait(false);
        if (node is null)
            return ServiceResult<Subgraph>.NotFound();

        var definition = graph.GetNodeTypeDefinition(typeNode);
        try {
            await EnsureNodeTypeSatisfiedByAsync(definition, node).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        var alreadyAssigned = await node.Nodes.AnyAsync(neighbor => neighbor.GlobalId == typeNode.GlobalId);
        if (!alreadyAssigned)
            await storage.Connect(nodeId, typeId).ConfigureAwait(false);

        var reloadedNode = await storage.Get(nodeId).ConfigureAwait(false);
        if (reloadedNode is null)
            return ServiceResult<Subgraph>.NotFound();

        return await GetSubgraph([nodeId, typeId], 1).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync(
        NodeRef source,
        NodeRef target,
        NodeRef typeId
    ) {
        var storage = graph.Storage;
        var typeState = await storage.Get(typeId).ConfigureAwait(false);
        if (typeState is null)
            return ServiceResult<Subgraph>.NotFound();
        var typeNode = await graph.AsNodeTypeAsync(typeState).ConfigureAwait(false);
        if (typeNode is null)
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not a node type.");
        var targetNode = await storage.Get(target).ConfigureAwait(false);
        if (targetNode is null)
            return ServiceResult<Subgraph>.NotFound();
        var disconnect = await DisconnectBasicEdgeIfPresentAsync(source, targetNode.GlobalId).ConfigureAwait(false);
        if (disconnect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(disconnect);
        var existingEdge = await storage.GetCommonIntersection(source, target, typeId).SingleOrDefaultAsync();
        if (existingEdge != null)
            await storage.Delete(existingEdge.GlobalId);

        return await CreateBasicTypedEdgeSubgraphAsync(source, target, typeNode.GlobalId);
    }

    public Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TNodeType>(NodeRef sourceGlobalId, NodeRef targetGlobalId) where TNodeType : NodeType {
        return ChangeEdgeTypeAsync<TNodeType>([sourceGlobalId, targetGlobalId]);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TNodeType>(IReadOnlyCollection<NodeRef> endpointIds) where TNodeType : NodeType {
        var storage = graph.Storage;
        var definitionResult = await GetTypedEdgeDefinitionAsync<TNodeType>().ConfigureAwait(false);
        if (definitionResult.Status != ServiceResultStatus.Ok || definitionResult.Value is null)
            return ServiceResult<Subgraph>.From(definitionResult);
        if (endpointIds.Count < 2)
            return ServiceResult<Subgraph>.BadRequest("Typed edge requires at least two endpoint nodes.");
        var definition = definitionResult.Value;
        var type = definition.NodeType.Type;
        var endpointStates = new List<NodeBacking>(endpointIds.Count);
        foreach (var endpointId in endpointIds) {
            var endpoint = await storage.Get(endpointId).ConfigureAwait(false);
            if (endpoint is null)
                return ServiceResult<Subgraph>.NotFound();
            endpointStates.Add(endpoint);
        }
        try {
            await EnsureTypedEdgeSatisfiedByAsync(definition, endpointStates).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        if (endpointIds.Count == 2) {
            var disconnect = await DisconnectBasicEdgeIfPresentAsync(endpointIds.ElementAt(0), endpointStates.ElementAt(1).GlobalId).ConfigureAwait(false);
            if (disconnect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(disconnect);
        }
        var relation = await storage.Create(
            await NextAvailableRootLocalIdAsync(RelationPrefix(definition.NodeType.Type.LocalId)).ConfigureAwait(false))
            .ConfigureAwait(false);
        await storage.Connect(relation.GlobalId, type.GlobalId).ConfigureAwait(false);

        var ensureEndpointType = graph.GetRequiredRuntimeType<EndpointNodeType>();

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstance = await storage.Create(
                new NodeLocalId($"endpoint-{endpoint.Name}"),
                relation.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, ensureEndpointType.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, endpointStates[index].GlobalId).ConfigureAwait(false);
            if (endpoint.NodeType is { } endpointType) {
                var endpointSpec = await storage.Create(
                    new NodeLocalId($"endpoint-spec-{endpoint.Name}"),
                    endpointInstance.GlobalId).ConfigureAwait(false);
                await storage.Connect(endpointSpec.GlobalId, endpointType.GlobalId).ConfigureAwait(false);
            }
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraph(IEnumerable<NodeRef> globalIds, int maxDepth) {
        var storage = graph.Storage;

        var requestedIds = globalIds.ToArray();
        var roots = new List<NodeBacking>();
        if (requestedIds.Length == 0)
            roots.AddRange(await storage.Root.Nodes.ToArrayAsync());
        else
            foreach (var rootRef in requestedIds) {
                var root = await storage.Get(rootRef);
                if (root is null)
                    return ServiceResult<Subgraph>.NotFound();
                if (root.GlobalId != storage.Root.GlobalId)
                    roots.Add(root);
                else
                    roots.AddRange(await storage.Root.Nodes.ToArrayAsync());
            }

        var visitedRequests = new HashSet<InternalId>();
        var visitedNodes = new HashSet<InternalId>();
        var discovered = new HashSet<InternalId>(roots.Select(x => x.GlobalId));
        var queue = new Queue<(InternalId NodeId, int Depth)>();

        foreach (var root in roots)
            queue.Enqueue((root.GlobalId, 0));

        var nodes = new Dictionary<InternalId, Node>();
        while (queue.Count > 0) {
            var (path, depth) = queue.Dequeue();
            if (!visitedRequests.Add(path))
                continue;
            var result = await storage.Get(path);
            if (result == null)
                continue;
            var node = new Node(result);
            if (!visitedNodes.Add(node.GlobalId))
                continue;
            nodes[node.GlobalId] = node;
            if (depth >= maxDepth)
                continue;
            await foreach (var neighborId in result.Nodes.Select(static x => x.GlobalId))
                if (neighborId != node.GlobalId && discovered.Add(neighborId))
                    queue.Enqueue((neighborId, depth + 1));
        }

        return ServiceResult<Subgraph>.Ok(nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            });
    }

    public async Task<ServiceResult<GraphObservation>> TraverseAsync(
        IEnumerable<NodeRef> rootIds,
        string traversal,
        int? maxNodes = null,
        int? maxEdges = null) {
        if (!string.Equals(traversal, "type-closure", StringComparison.OrdinalIgnoreCase))
            return ServiceResult<GraphObservation>.BadRequest($"Traversal '{traversal}' is not supported.");

        return await TraverseTypeClosureAsync(rootIds, maxNodes ?? 1000, maxEdges ?? 5000).ConfigureAwait(false);
    }

    public IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(
        NodeSearchQuery query,
        CancellationToken cancellationToken = default) {
        return searchService.SearchNodesStreamAsync(query, cancellationToken);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> GetNodeTypeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var typeNode = graph.GetRuntimeType<TNodeType>();
        if (typeNode is null)
            return ServiceResult<NodeTypeDefinition>.NotFound();

        var definition = graph.GetNodeTypeDefinition(typeNode);
        return ServiceResult<NodeTypeDefinition>.Ok(definition);
    }

    public async Task<ServiceResult<TypedEdgeDefinition>> GetTypedEdgeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var typeNode = graph.GetRuntimeType<TNodeType>();
        if (typeNode is null)
            return ServiceResult<TypedEdgeDefinition>.NotFound();

        var nodeTypeDefinition = graph.GetNodeTypeDefinition(typeNode);

        return TypedEdgeDefinition.TryCreate(nodeTypeDefinition, out var definition)
            ? ServiceResult<TypedEdgeDefinition>.Ok(definition)
            : ServiceResult<TypedEdgeDefinition>.BadRequest($"CLR type '{typeof(TNodeType).FullName}' does not define a typed edge node type.");
    }

    private static NodeRef? NormalizeParent(NodeRef? parent) {
        return parent switch {
            null => null,
            NodePath path when !path.Any() => null,
            InternalId id when !id.Any() => null,
            _ => parent
        };
    }

    private void AttachInstanceOf(Node instance, NodeType type) {
        if (instance.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Any(incidence => incidence.Type.GlobalId == type.GlobalId))
            return;

        _ = new InstanceOf(new InMemoryEdgeBacking(instance.Backing, type.Backing), instance, type);
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeRef sourceId, InternalId targetId) {
        var storage = graph.Storage;
        var node = await storage.Get(sourceId);
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Disconnect(sourceId, targetId);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<Subgraph>> CreateBasicTypedEdgeSubgraphAsync(NodeRef sourceId, NodeRef targetId, NodeRef typeId) {
        var storage = graph.Storage;
        var relation = await storage.Create(await NextAvailableRootLocalIdAsync("typed-edge").ConfigureAwait(false));
        await storage.Connect(relation.GlobalId, typeId);

        var sourcePort = await CreatePortAsync(relation.GlobalId, SourcePortRole);
        if (sourcePort.Status != ServiceResultStatus.Ok || sourcePort.Value is null)
            return ServiceResult<Subgraph>.From(sourcePort);
        var targetPort = await CreatePortAsync(relation.GlobalId, TargetPortRole);
        if (targetPort.Status != ServiceResultStatus.Ok || targetPort.Value is null)
            return ServiceResult<Subgraph>.From(targetPort);
        await storage.Connect(sourcePort.Value.GlobalId, sourceId);
        await storage.Connect(targetPort.Value.GlobalId, targetId);
        return await GetSubgraph([relation.GlobalId], 2);
    }

    private async Task<ServiceResult<NodeBacking>> CreatePortAsync(InternalId relationId, string role) {
        var storage = graph.Storage;
        var port = await storage.Create(new NodeLocalId(role), relationId).ConfigureAwait(false);
        var portType = graph.GetRequiredRuntimeType<PortNodeType>();
        await storage.Connect(port.GlobalId, portType.GlobalId).ConfigureAwait(false);
        return port;
    }

    private async Task<NodeLocalId> NextAvailableRootLocalIdAsync(string prefix) {
        var storage = graph.Storage;
        for (var index = 1; ; index++) {
            var candidate = new NodeLocalId($"{prefix}-{index}");
            if (await storage.Get(new NodePath(candidate)).ConfigureAwait(false) is null)
                return candidate;
        }
    }

    private static string RelationPrefix(NodeLocalId typeId) {
        var value = typeId.ToString();
        if (value.EndsWith("Connection", StringComparison.Ordinal))
            value = value[..^"Connection".Length];

        var chars = new List<char>();
        for (var index = 0; index < value.Length; index++) {
            var current = value[index];
            if (char.IsUpper(current) && index > 0)
                chars.Add('-');
            chars.Add(char.ToLowerInvariant(current));
        }

        return chars.Count == 0
            ? "typed-edge"
            : new string(chars.ToArray());
    }

    private async Task<ServiceResult> ValidateNodeTypeDefinitionRequest(
        NodeLocalId localId,
        IReadOnlyCollection<NodeFieldDefinition> fields,
        IReadOnlyCollection<NodeSlotDefinition> slots) {
        var localIdValidation = ValidateLocalId(localId, "Node type localId");
        if (localIdValidation is not null)
            return ServiceResult.BadRequest(localIdValidation);

        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields) {
            var fieldNameValidation = ValidateLocalId(new NodeLocalId(field.Name), $"Field '{field.Name}'");
            if (fieldNameValidation is not null)
                return ServiceResult.BadRequest(fieldNameValidation);
            if (ReservedDynamicTypeChildNames.Contains(field.Name))
                return ServiceResult.BadRequest($"Field name '{field.Name}' is reserved.");
            if (!fieldNames.Add(field.Name))
                return ServiceResult.BadRequest($"Field '{field.Name}' is declared more than once.");
            var cardinalityValidation = ValidateCardinality(field.Cardinality);
            if (cardinalityValidation is not null)
                return ServiceResult.BadRequest($"Field '{field.Name}' {cardinalityValidation}");
            if (!field.IsCollection && field.Cardinality.Max is null or > 1)
                return ServiceResult.BadRequest($"Field '{field.Name}' is not a collection, but allows more than one value.");

            if (field.ValueKind == NodeFieldValueKind.Node) {
                if (!typeof(Node).IsAssignableFrom(field.ClrType))
                    return ServiceResult.BadRequest($"Field '{field.Name}' is a node field but has CLR type '{field.ClrType.FullName}'.");
                if (field.NodeType is not null) {
                    var typeValidation = await EnsureExistingNodeType(field.NodeType).ConfigureAwait(false);
                    if (typeValidation.Status != ServiceResultStatus.Ok)
                        return typeValidation;
                }
                continue;
            }

            if (typeof(Node).IsAssignableFrom(field.ClrType))
                return ServiceResult.BadRequest($"Primitive field '{field.Name}' cannot use node CLR type '{field.ClrType.FullName}'.");
            if (field.NodeType is not null)
                return ServiceResult.BadRequest($"Primitive field '{field.Name}' cannot restrict a node type.");
        }

        var slotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots) {
            var slotNameValidation = ValidateLocalId(new NodeLocalId(slot.Name), $"Slot '{slot.Name}'");
            if (slotNameValidation is not null)
                return ServiceResult.BadRequest(slotNameValidation);
            if (ReservedDynamicTypeChildNames.Contains(slot.Name))
                return ServiceResult.BadRequest($"Slot name '{slot.Name}' is reserved.");
            if (!slotNames.Add(slot.Name))
                return ServiceResult.BadRequest($"Slot '{slot.Name}' is declared more than once.");
            var cardinalityValidation = ValidateCardinality(slot.Cardinality);
            if (cardinalityValidation is not null)
                return ServiceResult.BadRequest($"Slot '{slot.Name}' {cardinalityValidation}");
            if (slot.AllowedTypes.Count == 0)
                return ServiceResult.BadRequest($"Slot '{slot.Name}' must allow at least one node type.");
            foreach (var allowedType in slot.AllowedTypes) {
                var typeValidation = await EnsureExistingNodeType(allowedType).ConfigureAwait(false);
                if (typeValidation.Status != ServiceResultStatus.Ok)
                    return typeValidation;
            }
        }

        return ServiceResult.Ok();
    }

    private static NodeTypeDefinition CreateNodeTypeDefinition(
        NodeType type,
        bool isAbstract,
        IReadOnlyCollection<NodeFieldDefinition> fields,
        IReadOnlyCollection<NodeSlotDefinition> slots) {
        return new NodeTypeDefinition(
            type,
            isAbstract,
            slots.Concat(fields.Select(static field => field.ToSlotDefinition()).OfType<NodeSlotDefinition>()).ToArray(),
            fields.ToArray());
    }

    private async Task<ServiceResult> EnsureExistingNodeType(NodeType type) {
        var state = await graph.Storage.Get(type.GlobalId).ConfigureAwait(false);
        if (state is null)
            return ServiceResult.NotFound($"Node type '{type.GlobalId}' was not found.");
        var typeNode = await graph.AsNodeTypeAsync(state).ConfigureAwait(false);
        return typeNode is null
            ? ServiceResult.BadRequest($"Node '{type.GlobalId}' is not a node type.")
            : ServiceResult.Ok();
    }

    private static string? ValidateLocalId(NodeLocalId id, string subject) {
        var value = id.ToString();
        if (string.IsNullOrWhiteSpace(value))
            return $"{subject} is required.";
        if (value is "." or "..")
            return $"{subject} cannot be '.' or '..'.";
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            return $"{subject} contains characters that are not valid in node names.";
        return null;
    }

    private static string? ValidateCardinality(NodeSlotCardinality cardinality) {
        if (cardinality.Min < 0)
            return "cardinality minimum cannot be negative.";
        if (cardinality.Max is { } max && max < cardinality.Min)
            return "cardinality maximum cannot be lower than minimum.";
        return null;
    }

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) => new(result.Status, Error: result.Error);

    private async Task<ServiceResult<GraphObservation>> TraverseTypeClosureAsync(
        IEnumerable<NodeRef> rootIds,
        int maxNodes,
        int maxEdges) {
        if (maxNodes <= 0)
            return ServiceResult<GraphObservation>.BadRequest("maxNodes must be greater than zero.");
        if (maxEdges <= 0)
            return ServiceResult<GraphObservation>.BadRequest("maxEdges must be greater than zero.");

        var storage = graph.Storage;
        var requestedRoots = rootIds.ToArray();
        var builder = new GraphObservationBuilder("type-closure", maxNodes, maxEdges);
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(NodeBacking Node, string? Parent, string EdgeKind)>();

        if (requestedRoots.Length == 0) {
            builder.AddNode(graph.NodeTypes.Backing, TypeRootAttributes());
            await EnqueueTypeChildrenAsync(graph.NodeTypes.Backing, "catalogChild", builder, queue, queued).ConfigureAwait(false);
        } else {
            foreach (var rootRef in requestedRoots) {
                var root = await storage.Get(rootRef).ConfigureAwait(false);
                if (root is null)
                    return ServiceResult<GraphObservation>.NotFound();

                if (string.Equals(StableId(root), StableId(graph.NodeTypes.Backing), StringComparison.OrdinalIgnoreCase)) {
                    builder.AddNode(root, TypeRootAttributes());
                    await EnqueueTypeChildrenAsync(root, "catalogChild", builder, queue, queued).ConfigureAwait(false);
                } else {
                    EnqueueType(root, parent: null, "root", queue, queued);
                }
            }
        }

        while (queue.Count > 0 && builder.CanGrow) {
            var (state, parent, edgeKind) = queue.Dequeue();
            if (string.Equals(StableId(state), StableId(graph.NodeTypes.Backing), StringComparison.OrdinalIgnoreCase)
                || IsTypeDefinitionInfrastructureNode(state))
                continue;

            var type = new NodeType(state);
            var definition = graph.GetNodeTypeDefinition(type);
            AddTypeDefinitionObservation(builder, definition);
            if (parent is not null)
                builder.AddEdge(parent, StableId(state), edgeKind);

            await EnqueueTypeChildrenAsync(state, "catalogChild", builder, queue, queued).ConfigureAwait(false);
            foreach (var referencedType in GetReferencedTypes(definition))
                EnqueueType(referencedType.Backing, parent: null, "schemaReference", queue, queued);
        }

        if (queue.Count > 0)
            foreach (var (node, _, _) in queue)
                builder.AddBoundary(StableId(node), "quota");

        return ServiceResult<GraphObservation>.Ok(builder.Build());
    }

    private static async Task EnqueueTypeChildrenAsync(
        NodeBacking owner,
        string edgeKind,
        GraphObservationBuilder builder,
        Queue<(NodeBacking Node, string? Parent, string EdgeKind)> queue,
        HashSet<string> queued) {
        var ownerId = StableId(owner);
        await foreach (var neighbor in owner.Nodes.ConfigureAwait(false)) {
            if (!IsDirectChildOf(StableId(neighbor), ownerId) || IsTypeDefinitionInfrastructureNode(neighbor))
                continue;

            builder.AddNode(neighbor, TypeReferencePlaceholderAttributes());
            builder.AddEdge(ownerId, StableId(neighbor), edgeKind);
            EnqueueType(neighbor, ownerId, edgeKind, queue, queued);
        }
    }

    private static void EnqueueType(
        NodeBacking state,
        string? parent,
        string edgeKind,
        Queue<(NodeBacking Node, string? Parent, string EdgeKind)> queue,
        HashSet<string> queued) {
        if (queued.Add(StableId(state)))
            queue.Enqueue((state, parent, edgeKind));
    }

    private static void AddTypeDefinitionObservation(GraphObservationBuilder builder, NodeTypeDefinition definition) {
        var typeId = StableId(definition.Type.Backing);

        builder.AddNode(definition.Type.Backing, TypeNodeAttributes(definition));

        foreach (var field in definition.Fields) {
            var fieldId = SchemaMemberId(typeId, "field", field.Name);
            builder.AddNode(fieldId, field.Name, FieldAttributes(field));
            builder.AddEdge(typeId, fieldId, "schemaMember", MemberEdgeAttributes("field", field.Name));
            if (field.NodeType is not null) {
                builder.AddNode(field.NodeType.Backing, TypeReferencePlaceholderAttributes());
                builder.AddEdge(fieldId, StableId(field.NodeType.Backing), "schemaReference", MemberEdgeAttributes("field", field.Name));
            }
        }

        foreach (var slot in definition.Slots) {
            var slotId = SchemaMemberId(typeId, "slot", slot.Name);
            builder.AddNode(slotId, slot.Name, SlotAttributes(slot));
            builder.AddEdge(typeId, slotId, "schemaMember", MemberEdgeAttributes("slot", slot.Name));
            foreach (var allowedType in slot.AllowedTypes) {
                builder.AddNode(allowedType.Backing, TypeReferencePlaceholderAttributes());
                builder.AddEdge(slotId, StableId(allowedType.Backing), "schemaReference", MemberEdgeAttributes("slot", slot.Name));
            }
        }
    }

    private static string SchemaMemberId(string typeId, string memberKind, string memberName) =>
        $"{typeId}/@{memberKind}.{memberName}";

    private static Dictionary<string, string> MemberEdgeAttributes(string memberKind, string memberName) =>
        new(StringComparer.OrdinalIgnoreCase) {
            ["memberKind"] = memberKind,
            ["memberName"] = memberName
        };

    private static IEnumerable<NodeType> GetReferencedTypes(NodeTypeDefinition definition) {
        foreach (var field in definition.Fields)
            if (field.NodeType is not null)
                yield return field.NodeType;

        foreach (var slot in definition.Slots)
            foreach (var allowedType in slot.AllowedTypes)
                yield return allowedType;
    }

    private static Dictionary<string, string> TypeRootAttributes() =>
        new(StringComparer.OrdinalIgnoreCase) {
            ["graph.kind"] = "type-root"
        };

    private static Dictionary<string, string> TypeNodeAttributes(NodeTypeDefinition definition) {
        var attributes = new Dictionary<string, string>(definition.Type.Attributes, StringComparer.OrdinalIgnoreCase) {
            ["graph.kind"] = "type",
            ["isAbstract"] = definition.IsAbstract.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!attributes.ContainsKey("graph.element"))
            attributes["graph.element"] = TypedEdgeDefinition.TryCreate(definition, out _) ? "edge" : "node";
        return attributes;
    }

    private static Dictionary<string, string> TypeReferencePlaceholderAttributes() =>
        new(StringComparer.OrdinalIgnoreCase) {
            ["graph.kind"] = "type",
            ["graph.element"] = "node"
        };

    private static Dictionary<string, string> FieldAttributes(NodeFieldDefinition field) {
        var attributes = CardinalityAttributes(field.Cardinality);
        attributes["graph.kind"] = "schema-member";
        attributes["graph.role"] = "field";
        attributes["memberKind"] = "field";
        attributes["memberName"] = field.Name;
        attributes["valueKind"] = field.ValueKind.ToString();
        attributes["clrType"] = FormatClrType(field.ClrType);
        attributes["isCollection"] = field.IsCollection.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return attributes;
    }

    private static Dictionary<string, string> SlotAttributes(NodeSlotDefinition slot) {
        var attributes = CardinalityAttributes(slot.Cardinality);
        attributes["graph.kind"] = "schema-member";
        attributes["graph.role"] = "slot";
        attributes["memberKind"] = "slot";
        attributes["memberName"] = slot.Name;
        attributes["valueKind"] = NodeFieldValueKind.Node.ToString();
        attributes["isCollection"] = (slot.Cardinality.Max is null or > 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return attributes;
    }

    private static Dictionary<string, string> CardinalityAttributes(NodeSlotCardinality cardinality) {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["min"] = cardinality.Min.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (cardinality.Max is { } max)
            attributes["max"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return attributes;
    }

    private static string FormatClrType(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type).FullName ?? type.Name;

    private static bool IsDirectChildOf(string nodeId, string parentId) {
        var nodeSegments = nodeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentSegments = parentId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return nodeSegments.Length == parentSegments.Length + 1
            && parentSegments.SequenceEqual(nodeSegments.Take(parentSegments.Length), StringComparer.Ordinal);
    }

    private static bool IsTypeDefinitionInfrastructureNode(NodeBacking node) =>
        ReservedDynamicTypeChildNames.Contains(node.LocalId.ToString());

#pragma warning disable CS0618
    private static string StableId(NodeBacking node) => node.GlobalId.ToString();
#pragma warning restore CS0618

    private sealed class GraphObservationBuilder {
        readonly string traversal;
        readonly int maxNodes;
        readonly int maxEdges;
        readonly Dictionary<string, GraphObservationNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GraphObservationEdge> edges = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GraphObservationBoundary> boundary = new(StringComparer.OrdinalIgnoreCase);
        bool exhaustive = true;

        public GraphObservationBuilder(string traversal, int maxNodes, int maxEdges) {
            this.traversal = traversal;
            this.maxNodes = maxNodes;
            this.maxEdges = maxEdges;
        }

        public bool CanGrow => exhaustive && nodes.Count < maxNodes && edges.Count < maxEdges;

        public bool AddNode(NodeBacking node, IReadOnlyDictionary<string, string>? attributes = null) {
            return AddNode(
                StableId(node),
                node.LocalId.ToString(),
                MergeAttributes(node.Attributes, attributes));
        }

        public bool AddNode(
            string internalId,
            string localId,
            IEnumerable<KeyValuePair<string, string>>? attributes = null) {
            if (!nodes.TryGetValue(internalId, out var existing) && nodes.Count >= maxNodes) {
                AddBoundary(internalId, "maxNodes");
                exhaustive = false;
                return false;
            }

            var merged = new Dictionary<string, string>(
                existing?.Attributes ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            if (attributes is not null)
                foreach (var attribute in attributes)
                    merged[attribute.Key] = attribute.Value;

            nodes[internalId] = new GraphObservationNode(
                localId,
                internalId,
                merged);
            return true;
        }

        public bool AddEdge(
            string left,
            string right,
            string kind,
            IEnumerable<KeyValuePair<string, string>>? attributes = null) {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!nodes.ContainsKey(left) || !nodes.ContainsKey(right))
                return false;

            var key = EdgeKey(left, right, kind);
            if (!edges.ContainsKey(key) && edges.Count >= maxEdges) {
                AddBoundary($"{left} -> {right}", "maxEdges");
                exhaustive = false;
                return false;
            }

            edges[key] = new GraphObservationEdge(
                LocalIdFromInternalId(left),
                left,
                LocalIdFromInternalId(right),
                right,
                kind,
                attributes?.ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>());
            return true;
        }

        public void AddBoundary(string internalId, string reason) {
            boundary[internalId] = new GraphObservationBoundary(internalId, reason);
        }

        public GraphObservation Build() {
            return new GraphObservation(
                traversal,
                nodes.Values
                    .OrderBy(static node => node.InternalId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                edges.Values
                    .OrderBy(static edge => edge.Node1InternalId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.Node2InternalId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.Kind, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                boundary.Values
                    .OrderBy(static item => item.InternalId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                exhaustive);
        }

        private static Dictionary<string, string> MergeAttributes(
            IDictionary<string, string> first,
            IReadOnlyDictionary<string, string>? second) {
            var result = new Dictionary<string, string>(first, StringComparer.OrdinalIgnoreCase);
            if (second is not null)
                foreach (var item in second)
                    result[item.Key] = item.Value;
            return result;
        }

        private static string EdgeKey(string left, string right, string kind) {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
                ? $"{kind}\0{left}\0{right}"
                : $"{kind}\0{right}\0{left}";
        }

        private static string LocalIdFromInternalId(string internalId) {
            var segments = internalId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0 ? internalId : segments[^1];
        }
    }

    public async Task<NodeType?> GetTypeNode(NodeRef id) {
        var state = await graph.Storage.Get(id).ConfigureAwait(false);
        if (state is null)
            return null;
        return await graph.AsNodeTypeAsync(state).ConfigureAwait(false);
    }

    public async Task<NodeType?> GetTypeNode(Node node) {
        return await graph.AsNodeTypeAsync(node.Backing).ConfigureAwait(false);
    }

    private async Task EnsureNodeTypeSatisfiedByAsync(NodeTypeDefinition definition, NodeBacking instance) {
        foreach (var slot in definition.Slots)
            await EnsureSlotSatisfiedByAsync(slot, definition.Type, instance).ConfigureAwait(false);
    }

    private static async Task EnsureSlotSatisfiedByAsync(NodeSlotDefinition slot, NodeType instanceType, NodeBacking instance) {
        var allowedTypeIds = slot.AllowedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        var count = 0;
        await foreach (var neighbor in instance.Nodes.ConfigureAwait(false)) {
            if (neighbor.GlobalId == instanceType.GlobalId)
                continue;
            if (await HasAnyTypeAsync(neighbor, allowedTypeIds).ConfigureAwait(false))
                count++;
        }

        if (!slot.Cardinality.Contains(count))
            throw new InvalidOperationException($"Slot '{slot.Name}' expects {slot.Cardinality} linked nodes, but found {count}.");
    }

    private static async Task EnsureTypedEdgeSatisfiedByAsync(TypedEdgeDefinition definition, IReadOnlyCollection<NodeBacking> endpoints) {
        if (definition.Endpoints.Count < 2)
            throw new InvalidOperationException($"Typed edge node type '{definition.Type.GlobalId}' must define at least two endpoints.");

        if (endpoints.Count != definition.Endpoints.Count)
            throw new InvalidOperationException(
                $"Typed edge node type '{definition.Type.GlobalId}' expects {definition.Endpoints.Count} endpoints, but got {endpoints.Count}.");

        var endpointStates = endpoints.ToArray();
        var endpointDefinitions = definition.Endpoints.ToArray();
        for (var index = 0; index < endpointDefinitions.Length; index++) {
            var endpoint = endpointDefinitions[index];
            if (endpoint.NodeType is null)
                continue;

            var instance = endpointStates[index];
            if (!await HasTypeAsync(instance, endpoint.NodeType).ConfigureAwait(false))
                throw new InvalidOperationException($"Endpoint '{endpoint.Name}' expects node type '{endpoint.NodeType.GlobalId}', but node '{instance.GlobalId}' has another type.");
        }
    }

    private static Task<bool> HasTypeAsync(NodeBacking node, NodeType type) {
        return HasAnyTypeAsync(node, new HashSet<InternalId> { type.GlobalId });
    }

    private static async Task<bool> HasAnyTypeAsync(NodeBacking node, IReadOnlySet<InternalId> typeIds) {
        await foreach (var type in node.Nodes.ConfigureAwait(false))
            if (typeIds.Contains(type.GlobalId))
                return true;

        return false;
    }

}
