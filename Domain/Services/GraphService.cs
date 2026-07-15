using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
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

    internal Graph Model => graph;

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNodeCore(graph, localId, path, type, attributes).ConfigureAwait(false);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> CreateNodeType(
        NodeLocalId localId,
        bool isAbstract = false,
        IEnumerable<NodeFieldDefinition>? fields = null,
        IEnumerable<NodeSlotDefinition>? slots = null,
        IEnumerable<NodeType>? requiredTypes = null) =>
        await CreateGraphType(localId, GraphElementKind.Node, isAbstract, fields, slots, requiredTypes).ConfigureAwait(false);

    public async Task<ServiceResult<NodeTypeDefinition>> CreateEdgeType(
        NodeLocalId localId,
        bool isAbstract = false,
        IEnumerable<NodeFieldDefinition>? fields = null,
        IEnumerable<NodeSlotDefinition>? slots = null,
        IEnumerable<NodeType>? requiredTypes = null) =>
        await CreateGraphType(localId, GraphElementKind.Edge, isAbstract, fields, slots, requiredTypes).ConfigureAwait(false);

    private async Task<ServiceResult<NodeTypeDefinition>> CreateGraphType(
        NodeLocalId localId,
        GraphElementKind elementKind,
        bool isAbstract,
        IEnumerable<NodeFieldDefinition>? fields,
        IEnumerable<NodeSlotDefinition>? slots,
        IEnumerable<NodeType>? requiredTypes) {
        var fieldArray = fields?.ToArray() ?? [];
        var slotArray = slots?.ToArray() ?? [];
        var requiredTypeArray = requiredTypes?
            .DistinctBy(static type => type.GlobalId)
            .ToArray() ?? [];

        var validation = await ValidateNodeTypeDefinitionRequest(localId, fieldArray, slotArray).ConfigureAwait(false);
        if (validation.Status != ServiceResultStatus.Ok)
            return new ServiceResult<NodeTypeDefinition>(validation.Status, Error: validation.Error);
        foreach (var requiredType in requiredTypeArray) {
            validation = await EnsureExistingNodeType(requiredType).ConfigureAwait(false);
            if (validation.Status != ServiceResultStatus.Ok)
                return new ServiceResult<NodeTypeDefinition>(validation.Status, Error: validation.Error);
            if (graph.GetNodeTypeDefinition(requiredType).ElementKind != elementKind)
                return ServiceResult<NodeTypeDefinition>.BadRequest(
                    $"Graph type '{localId}' cannot inherit '{requiredType.GlobalId}' because NodeType and Edge hierarchies are separate.");
        }

        var storage = graph.Storage;
        var existing = await storage.Get(graph.NodeTypes.GlobalId, localId).ConfigureAwait(false);
        if (existing is not null)
            return ServiceResult<NodeTypeDefinition>.Conflict($"Node type '{localId}' already exists.");

        NodeBacking? created = null;
        try {
            created = await graph.SemanticStorage.CreateTypeElementAsync(
                localId,
                graph.NodeTypes.GlobalId,
                SemanticElementKind.Type,
                requiredTypeArray.Select(static requiredType => (NodeRef)requiredType.GlobalId).ToArray())
                .ConfigureAwait(false);
            var type = new NodeType(created);
            var definition = CreateNodeTypeDefinition(type, elementKind, isAbstract, fieldArray, slotArray, requiredTypeArray);
            await DynamicNodeTypeDefinitionStorage.WriteAsync(graph.SemanticStorage, definition).ConfigureAwait(false);
            graph.RegisterNodeTypeDefinition(definition);

            return ServiceResult<NodeTypeDefinition>.Ok(graph.GetNodeTypeDefinition(type));
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
        var reloaded = await storage.Get(node.GlobalId).ConfigureAwait(false);
        return reloaded is null
            ? ServiceResult<Node>.NotFound()
            : ServiceResult<Node>.Ok(await ReadSemanticNodeAsync(reloaded).ConfigureAwait(false));
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
        var source = await storage.Get(path).ConfigureAwait(false);
        if (source is null)
            return ServiceResult<Node>.NotFound();

        var matches = (await source.Nodes.ToArrayAsync().ConfigureAwait(false))
            .Where(candidate => candidate.LocalId == localId)
            .DistinctBy(static candidate => candidate.GlobalId)
            .Take(2)
            .ToArray();
        return matches.Length switch {
            0 => ServiceResult<Node>.NotFound(),
            1 => ServiceResult<Node>.Ok(new Node(matches[0])),
            _ => ServiceResult<Node>.BadRequest(
                $"Node '{path}' has more than one graph neighbor with LocalId '{localId}'; use GlobalId to disambiguate.")
        };
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

        // The semantic storage owns relation integrity and removes every
        // incident relation through its rebuildable incidence index.
        await storage.Delete(node).ConfigureAwait(false);
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
        var typeDefinition = graph.GetNodeTypeDefinition(typeNode);
        if (typeDefinition.IsAbstract)
            return ServiceResult<Subgraph>.BadRequest($"Abstract graph type '{typeNode.GlobalId}' cannot be assigned directly.");

        var node = await storage.Get(nodeId).ConfigureAwait(false);
        if (node is null)
            return ServiceResult<Subgraph>.NotFound();
        if (node is not ISemanticNodeBacking semanticNode)
            return ServiceResult<Subgraph>.BadRequest(
                $"Node '{node.GlobalId}' does not expose a semantic storage kind.");

        var expectedKind = typeDefinition.ElementKind switch {
            GraphElementKind.Node => SemanticElementKind.Instance,
            GraphElementKind.Edge => SemanticElementKind.Relation,
            _ => throw new ArgumentOutOfRangeException(nameof(typeDefinition.ElementKind))
        };
        if (semanticNode.SemanticKind != expectedKind)
            return ServiceResult<Subgraph>.BadRequest(
                $"Graph type '{typeNode.GlobalId}' has element kind '{typeDefinition.ElementKind}' "
                + $"and can only classify a semantic {expectedKind}, not '{semanticNode.SemanticKind}'.");

        IReadOnlyCollection<NodeType> effectiveTypes;
        try {
            effectiveTypes = GetRequiredTypeClosure(typeNode);
            var effectiveDefinitions = effectiveTypes
                .Select(graph.GetNodeTypeDefinition)
                .ToArray();
            var incompatibleRequirement = effectiveDefinitions.FirstOrDefault(
                definition => definition.ElementKind != typeDefinition.ElementKind);
            if (incompatibleRequirement is not null)
                throw new InvalidOperationException(
                    $"Graph type '{typeNode.GlobalId}' requires '{incompatibleRequirement.Type.GlobalId}' "
                    + $"with incompatible element kind '{incompatibleRequirement.ElementKind}'.");
            if (semanticNode.SemanticKind == SemanticElementKind.Instance)
                EnsureInstanceTypeHasNoRequiredNamedNodeFields(typeNode, effectiveDefinitions);
            else
                await EnsureRelationTypeSatisfiedByAsync(node, typeNode, typeDefinition).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        try {
            var declaredTypeIds = graph.SemanticStorage.GetDeclaredTypeIds(node)
                .Append(typeNode.GlobalId)
                .Distinct()
                .Select(static id => (NodeRef)id)
                .ToArray();
            await graph.SemanticStorage.SetDeclaredTypeIdsAsync(node.GlobalId, declaredTypeIds).ConfigureAwait(false);
            return await GetSubgraph([node.GlobalId], 1).ConfigureAwait(false);
        } catch (Exception ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<Subgraph>> AddNodeTypeRequirementAsync(
        NodeRef derivedTypeId,
        NodeRef requiredTypeId) {
        var derivedType = await GetTypeNode(derivedTypeId).ConfigureAwait(false);
        var requiredType = await GetTypeNode(requiredTypeId).ConfigureAwait(false);
        if (derivedType is null || requiredType is null)
            return ServiceResult<Subgraph>.NotFound("Both derived and required nodes must be graph types.");

        try {
            var requiredTypeIds = graph.SemanticStorage.GetRequiredTypeIds(derivedType.Backing)
                .Append(requiredType.GlobalId)
                .Distinct()
                .Select(static id => (NodeRef)id)
                .ToArray();
            await graph.SemanticStorage.SetRequiredTypeIdsAsync(
                derivedType.GlobalId,
                requiredTypeIds).ConfigureAwait(false);

            var refreshedDefinition = graph.GetNodeTypeDefinition(derivedType);
            graph.RegisterNodeTypeDefinition(refreshedDefinition with {
                RequiredTypes = refreshedDefinition.RequiredTypes
                    .Append(requiredType)
                    .DistinctBy(static type => type.GlobalId)
                    .ToArray()
            });
            return await GetSubgraph([derivedType.GlobalId, requiredType.GlobalId], 1).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<InstanceNode>> GetSemanticNodeAsync(NodeRef nodeId) {
        var state = await graph.Storage.Get(nodeId).ConfigureAwait(false);
        if (state is null)
            return ServiceResult<InstanceNode>.NotFound();

        try {
            return ServiceResult<InstanceNode>.Ok(
                await ReadSemanticNodeAsync(state).ConfigureAwait(false));
        } catch (InvalidOperationException ex) {
            return ServiceResult<InstanceNode>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>> GetEffectiveTypeDefinitionsAsync(
        NodeRef typeId) {
        var type = await GetTypeNode(typeId).ConfigureAwait(false);
        if (type is null)
            return ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>.NotFound(
                $"Node '{typeId}' was not found or is not a graph type.");

        try {
            return ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>.Ok(
                GetRequiredTypeClosure(type)
                    .Select(graph.GetNodeTypeDefinition)
                    .ToArray());
        } catch (InvalidOperationException ex) {
            return ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<TypedEdgeInstance>> GetTypedEdgeInstanceAsync(NodeRef relationId) {
        var relation = await graph.Storage.Get(relationId).ConfigureAwait(false);
        if (relation is null)
            return ServiceResult<TypedEdgeInstance>.NotFound();

        try {
            var semanticRelation = await graph.SemanticStorage.GetRelationAsync(relationId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Node '{relationId}' is not a semantic relation.");
            var definition = (await ResolveTypedEdgeClassifiersAsync(semanticRelation.TypeIds).ConfigureAwait(false))
                .Single();
            return ServiceResult<TypedEdgeInstance>.Ok(
                await TypedEdgeSubgraphCodec.ReadAsync(graph, relation, definition).ConfigureAwait(false));
        } catch (InvalidOperationException ex) {
            return ServiceResult<TypedEdgeInstance>.BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Creates a canonical n-ary semantic relation. The relation document owns
    /// the complete named member list; no carrier or endpoint nodes are stored.
    /// </summary>
    public async Task<ServiceResult<TypedEdgeInstance>> CreateRelationAsync(
        NodeLocalId relationLocalId,
        NodeRef typeId,
        IReadOnlyDictionary<string, IReadOnlyCollection<NodeRef>> members) {
        var typeState = await graph.Storage.Get(typeId).ConfigureAwait(false);
        if (typeState is null)
            return ServiceResult<TypedEdgeInstance>.NotFound($"Relation type '{typeId}' was not found.");
        if (!graph.SemanticStorage.IsType(typeState))
            return ServiceResult<TypedEdgeInstance>.BadRequest($"Node '{typeId}' is not a graph type.");

        var type = new NodeType(typeState);
        if (!TypedEdgeDefinition.TryCreate(graph.GetNodeTypeDefinition(type), out var definition))
            return ServiceResult<TypedEdgeInstance>.BadRequest(
                $"Graph type '{type.GlobalId}' is not an edge type with at least two named members.");

        var unknownMembers = members.Keys
            .Except(definition.Endpoints.Select(static endpoint => endpoint.Name), StringComparer.Ordinal)
            .ToArray();
        if (unknownMembers.Length > 0)
            return ServiceResult<TypedEdgeInstance>.BadRequest(
                $"Relation contains members not declared by '{type.GlobalId}': {string.Join(", ", unknownMembers)}.");

        var participantBackings = new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal);
        try {
            foreach (var endpoint in definition.Endpoints) {
                if (!members.TryGetValue(endpoint.Name, out var participantIds))
                    return ServiceResult<TypedEdgeInstance>.BadRequest($"Relation member '{endpoint.Name}' is missing.");
                var uniqueIds = participantIds
                    .Select(static id => id)
                    .DistinctBy(static id => id.ToString())
                    .ToArray();
                if (!endpoint.Cardinality.Contains(uniqueIds.Length))
                    return ServiceResult<TypedEdgeInstance>.BadRequest(
                        $"Relation member '{endpoint.Name}' expects {endpoint.Cardinality}, but got {uniqueIds.Length}.");

                var participants = new List<NodeBacking>(uniqueIds.Length);
                foreach (var participantId in uniqueIds) {
                    var participant = await graph.Storage.Get(participantId).ConfigureAwait(false);
                    if (participant is null)
                        return ServiceResult<TypedEdgeInstance>.NotFound(
                            $"Relation participant '{participantId}' was not found.");
                    if (endpoint.NodeType is not null
                        && !await HasTypeAsync(participant, endpoint.NodeType).ConfigureAwait(false))
                        return ServiceResult<TypedEdgeInstance>.BadRequest(
                            $"Relation member '{endpoint.Name}' expects '{endpoint.NodeType.GlobalId}', but participant '{participant.GlobalId}' has another type.");
                    participants.Add(participant);
                }
                participantBackings[endpoint.Name] = participants;
            }

            var relation = await TypedEdgeSubgraphCodec.CreateAsync(
                graph.Storage,
                relationLocalId,
                definition,
                participantBackings).ConfigureAwait(false);
            return ServiceResult<TypedEdgeInstance>.Ok(
                await TypedEdgeSubgraphCodec.ReadAsync(graph, relation, definition).ConfigureAwait(false));
        } catch (InvalidOperationException ex) {
            return ServiceResult<TypedEdgeInstance>.BadRequest(ex.Message);
        }
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
        var typeDefinition = graph.GetNodeTypeDefinition(typeNode);
        if (!TypedEdgeDefinition.TryCreate(typeDefinition, out var definition))
            return ServiceResult<Subgraph>.BadRequest(
                $"Node type '{typeNode.GlobalId}' must define exactly two named node endpoints to be used as a basic typed edge.");
        if (definition.Endpoints.Count != 2)
            return ServiceResult<Subgraph>.BadRequest(
                $"Node type '{typeNode.GlobalId}' must define exactly two named node endpoints to be used as a basic typed edge, but defines {definition.Endpoints.Count}.");

        var sourceNode = await storage.Get(source).ConfigureAwait(false);
        if (sourceNode is null)
            return ServiceResult<Subgraph>.NotFound();
        var targetNode = await storage.Get(target).ConfigureAwait(false);
        if (targetNode is null)
            return ServiceResult<Subgraph>.NotFound();

        try {
            await EnsureTypedEdgeSatisfiedByAsync(definition, [sourceNode, targetNode]).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        return await MaterializeTypedEdgeThenDisconnectBasicEdgeAsync(
            definition,
            [sourceNode, targetNode]).ConfigureAwait(false);
    }

    public Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TEdgeType>(NodeRef sourceGlobalId, NodeRef targetGlobalId) where TEdgeType : Edge {
        return ChangeEdgeTypeAsync<TEdgeType>([sourceGlobalId, targetGlobalId]);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TEdgeType>(IReadOnlyCollection<NodeRef> endpointIds) where TEdgeType : Edge {
        var storage = graph.Storage;
        var definitionResult = await GetTypedEdgeDefinitionAsync<TEdgeType>().ConfigureAwait(false);
        if (definitionResult.Status != ServiceResultStatus.Ok || definitionResult.Value is null)
            return ServiceResult<Subgraph>.From(definitionResult);
        if (endpointIds.Count < 2)
            return ServiceResult<Subgraph>.BadRequest("Typed edge requires at least two endpoint nodes.");
        var definition = definitionResult.Value;
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

        return await MaterializeTypedEdgeThenDisconnectBasicEdgeAsync(
            definition,
            endpointStates).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraph(IEnumerable<NodeRef> globalIds, int maxDepth) {
        var storage = graph.Storage;

        var requestedIds = globalIds.ToArray();
        var roots = new List<NodeBacking>();
        if (requestedIds.Length == 0) {
            roots.Add(graph.SemanticStorage.TypeRoot);
            await foreach (var instanceRoot in graph.SemanticStorage
                               .EnumerateInstanceNamespaceChildrenAsync()
                               .ConfigureAwait(false))
                roots.Add(instanceRoot);
        }
        else
            foreach (var rootRef in requestedIds) {
                var root = await storage.Get(rootRef);
                if (root is null)
                    return ServiceResult<Subgraph>.NotFound();
                if (root.GlobalId != storage.Root.GlobalId)
                    roots.Add(root);
                else {
                    roots.Add(graph.SemanticStorage.TypeRoot);
                    await foreach (var instanceRoot in graph.SemanticStorage
                                       .EnumerateInstanceNamespaceChildrenAsync()
                                       .ConfigureAwait(false))
                        roots.Add(instanceRoot);
                }
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

    /// <summary>
    /// Reads the complete, separate type catalog without traversing the
    /// instance graph. This is intentionally an explicit global operation over
    /// types; ordinary instance reads remain local.
    /// </summary>
    public async Task<ServiceResult<Subgraph>> GetTypeCatalogSubgraphAsync(
        CancellationToken cancellationToken = default) {
        var nodes = new Dictionary<InternalId, Node>();
        await foreach (var state in graph.SemanticStorage
                           .EnumerateTypeElementsAsync(cancellationToken)
                           .ConfigureAwait(false))
            nodes[state.GlobalId] = new Node(state);

        return ServiceResult<Subgraph>.Ok(new Subgraph { Nodes = nodes.Values });
    }

    public async Task<ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>> GetTypeDefinitionsAsync(IEnumerable<NodeRef>? rootIds = null) {
        var storage = graph.Storage;
        var requestedRoots = rootIds?.ToArray() ?? [];
        var queue = new Queue<NodeBacking>();
        var queued = new HashSet<InternalId>();

        if (requestedRoots.Length == 0) {
            await EnqueueTypeCatalogChildrenAsync(graph.NodeTypes.Backing, queue, queued).ConfigureAwait(false);
        } else {
            foreach (var rootRef in requestedRoots) {
                var root = await storage.Get(rootRef).ConfigureAwait(false);
                if (root is null)
                    return ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>.NotFound();

                if (root.GlobalId == graph.NodeTypes.GlobalId)
                    await EnqueueTypeCatalogChildrenAsync(root, queue, queued).ConfigureAwait(false);
                else
                    Enqueue(root, queue, queued);
            }
        }

        var definitions = new Dictionary<InternalId, NodeTypeDefinition>();
        while (queue.Count > 0) {
            var state = queue.Dequeue();
            if (state.GlobalId == graph.NodeTypes.GlobalId || IsTypeDefinitionInfrastructureNode(state))
                continue;

            var type = new NodeType(state);
            var definition = graph.GetNodeTypeDefinition(type);
            if (!definitions.TryAdd(type.GlobalId, definition))
                continue;

            await EnqueueTypeCatalogChildrenAsync(state, queue, queued).ConfigureAwait(false);
            foreach (var referencedType in GetReferencedTypes(definition))
                Enqueue(referencedType.Backing, queue, queued);
        }

        return ServiceResult<IReadOnlyCollection<NodeTypeDefinition>>.Ok(
            definitions.Values
                .OrderBy(static definition => definition.Type.GlobalId.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray());
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

    public async Task<ServiceResult<TypedEdgeDefinition>> GetTypedEdgeDefinitionAsync<TEdgeType>()
        where TEdgeType : Edge {
        var typeNode = graph.GetRuntimeType<TEdgeType>();
        if (typeNode is null)
            return ServiceResult<TypedEdgeDefinition>.NotFound();

        var nodeTypeDefinition = graph.GetNodeTypeDefinition(typeNode);

        return TypedEdgeDefinition.TryCreate(nodeTypeDefinition, out var definition)
            ? ServiceResult<TypedEdgeDefinition>.Ok(definition)
            : ServiceResult<TypedEdgeDefinition>.BadRequest($"CLR type '{typeof(TEdgeType).FullName}' does not define a typed edge type.");
    }

    private static NodeRef? NormalizeParent(NodeRef? parent) {
        return parent switch {
            null => null,
            NodePath path when !path.Any() => null,
            InternalId id when !id.Any() => null,
            _ => parent
        };
    }

    private async Task<InstanceNode> ReadSemanticNodeAsync(NodeBacking state) {
        var declaredTypes = new List<NodeType>();
        foreach (var typeId in graph.SemanticStorage.GetDeclaredTypeIds(state)) {
            var typeState = await graph.Storage.Get(typeId).ConfigureAwait(false);
            if (typeState is null || !graph.SemanticStorage.IsType(typeState))
                continue; // TypeRef is intentionally weak and may be dangling.
            declaredTypes.Add(new NodeType(typeState));
        }

        var effectiveTypes = declaredTypes
            .SelectMany(GetRequiredTypeClosure)
            .DistinctBy(static type => type.GlobalId)
            .Select(static type => new NodeTypeInstance(type))
            .ToArray();

        return new InstanceNode(state, effectiveTypes, declaredTypes);
    }

    private IReadOnlyCollection<NodeType> GetRequiredTypeClosure(NodeType assignedType) {
        var result = new List<NodeType>();
        var visited = new HashSet<InternalId>();
        var stack = new Stack<NodeType>();
        stack.Push(assignedType);
        while (stack.Count > 0) {
            var type = stack.Pop();
            if (!visited.Add(type.GlobalId))
                continue;
            result.Add(type);
            foreach (var requiredType in graph.GetNodeTypeDefinition(type).RequiredTypes.Reverse())
                stack.Push(requiredType);
        }
        return result;
    }

    private async Task<IReadOnlyCollection<TypedRelationCandidate>> FindIncidentTypedRelationsAsync(
        NodeBacking participant) {
        var relations = new Dictionary<InternalId, TypedRelationCandidate>();
        await foreach (var relation in graph.SemanticStorage.FindIncidentRelationsAsync(
                           participant.GlobalId).ConfigureAwait(false)) {
            var classifiers = await ResolveTypedEdgeClassifiersAsync(relation.TypeIds).ConfigureAwait(false);
            if (classifiers.Count == 0)
                continue;
            if (classifiers.Count != 1)
                throw new InvalidOperationException(
                    $"Typed edge '{relation.Id}' must have exactly one typed-edge classifier, but found {classifiers.Count}.");
            var backing = await graph.Storage.Get(relation.Id).ConfigureAwait(false);
            if (backing is not null)
                relations[backing.GlobalId] = new TypedRelationCandidate(backing, classifiers.Single());
        }

        return relations.Values.ToArray();
    }

    private async Task<IReadOnlyCollection<NodeBacking>> FindTypedRelationsForParticipantsAsync(
        IReadOnlyCollection<NodeBacking> participants) {
        if (participants.Count == 0)
            return Array.Empty<NodeBacking>();

        var expectedParticipantIds = participants
            .Select(static participant => participant.GlobalId.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var matches = new Dictionary<InternalId, NodeBacking>();
        var incidentRelations = await FindIncidentTypedRelationsAsync(participants.First()).ConfigureAwait(false);
        foreach (var relation in incidentRelations) {
            var typedEdge = await TypedEdgeSubgraphCodec.ReadAsync(
                graph,
                relation.Backing,
                relation.Definition).ConfigureAwait(false);
            var actualParticipantIds = typedEdge.Endpoints
                .SelectMany(static endpoint => endpoint.Participants)
                .Select(static participant => participant.GlobalId.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (expectedParticipantIds.SequenceEqual(actualParticipantIds, StringComparer.Ordinal))
                matches[relation.Backing.GlobalId] = relation.Backing;
        }

        return matches.Values.ToArray();
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeRef sourceId, InternalId targetId) {
        var storage = graph.Storage;
        var node = await storage.Get(sourceId);
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Disconnect(sourceId, targetId);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<Subgraph>> MaterializeTypedEdgeThenDisconnectBasicEdgeAsync(
        TypedEdgeDefinition definition,
        IReadOnlyList<NodeBacking> endpointStates) {
        var storage = graph.Storage;
        IReadOnlyCollection<NodeBacking> existingRelations;
        try {
            existingRelations = await FindTypedRelationsForParticipantsAsync(endpointStates).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }
        if (existingRelations.Count > 1)
            return ServiceResult<Subgraph>.BadRequest(
                $"More than one typed edge connects the selected participants; the edge to replace is ambiguous.");
        var existingRelation = existingRelations.SingleOrDefault();

        var endpoints = definition.Endpoints.ToArray();
        var participants = new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal);
        for (var index = 0; index < endpoints.Length; index++)
            participants[endpoints[index].Name] = [endpointStates[index]];

        var relation = await TypedEdgeSubgraphCodec.CreateAsync(
            storage,
            NewRootRelationLocalId(RelationPrefix(definition.Type.LocalId)),
            definition,
            participants).ConfigureAwait(false);

        if (endpointStates.Count == 2) {
            try {
                var disconnect = await DisconnectBasicEdgeIfPresentAsync(
                    endpointStates[0].GlobalId,
                    endpointStates[1].GlobalId).ConfigureAwait(false);
                if (disconnect.Status != ServiceResultStatus.Ok) {
                    await storage.Delete(relation).ConfigureAwait(false);
                    return ToSubgraphResult(disconnect);
                }
            } catch (Exception ex) {
                await storage.Delete(relation).ConfigureAwait(false);
                return ServiceResult<Subgraph>.BadRequest(
                    $"Typed edge was not applied because the original raw edge could not be removed: {ex.Message}");
            }
        }

        if (existingRelation is not null) {
            try {
                await storage.Delete(existingRelation).ConfigureAwait(false);
            } catch (Exception ex) {
                await storage.Delete(relation).ConfigureAwait(false);
                return ServiceResult<Subgraph>.BadRequest(
                    $"Typed edge was not replaced because the previous compatibility relation could not be removed: {ex.Message}");
            }
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    private static NodeLocalId NewRootRelationLocalId(string prefix) =>
        new($"{prefix}-{Guid.NewGuid():N}");

    private static string RelationPrefix(NodeLocalId typeId) {
        var value = typeId.ToString();
        if (value.EndsWith("Connection", StringComparison.Ordinal))
            value = value[..^"Connection".Length];

        return ToKebabIdentifier(value);
    }

    private static string ToKebabIdentifier(string value) {
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
        GraphElementKind elementKind,
        bool isAbstract,
        IReadOnlyCollection<NodeFieldDefinition> fields,
        IReadOnlyCollection<NodeSlotDefinition> slots,
        IReadOnlyCollection<NodeType> requiredTypes) {
        return new NodeTypeDefinition(
            type,
            isAbstract,
            slots.Concat(fields.Select(static field => field.ToSlotDefinition()).OfType<NodeSlotDefinition>()).ToArray(),
            fields.ToArray()) {
            ElementKind = elementKind,
            RequiredTypes = requiredTypes.ToArray()
        };
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

    private static async Task EnqueueTypeCatalogChildrenAsync(
        NodeBacking owner,
        Queue<NodeBacking> queue,
        HashSet<InternalId> queued) {
        await foreach (var neighbor in owner.Nodes.ConfigureAwait(false)) {
            if (IsDirectChildOf(neighbor.GlobalId, owner.GlobalId) && !IsTypeDefinitionInfrastructureNode(neighbor))
                Enqueue(neighbor, queue, queued);
        }
    }

    private static void Enqueue(NodeBacking state, Queue<NodeBacking> queue, HashSet<InternalId> queued) {
        if (queued.Add(state.GlobalId))
            queue.Enqueue(state);
    }

    private static IEnumerable<NodeType> GetReferencedTypes(NodeTypeDefinition definition) {
        foreach (var requiredType in definition.RequiredTypes)
            yield return requiredType;

        foreach (var field in definition.Fields)
            if (field.NodeType is not null)
                yield return field.NodeType;

        foreach (var slot in definition.Slots)
            foreach (var allowedType in slot.AllowedTypes)
                yield return allowedType;
    }

    private static bool IsDirectChildOf(InternalId nodeId, InternalId parentId) {
        var nodeSegments = nodeId.Select(static segment => segment.ToString()).ToArray();
        var parentSegments = parentId.Select(static segment => segment.ToString()).ToArray();
        return nodeSegments.Length == parentSegments.Length + 1
            && parentSegments.SequenceEqual(nodeSegments.Take(parentSegments.Length), StringComparer.Ordinal);
    }

    private static bool IsTypeDefinitionInfrastructureNode(NodeBacking node) {
        return node is ISemanticNodeBacking {
            SemanticKind: SemanticElementKind.TypeInfrastructure
        };
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

    private async Task<IReadOnlyCollection<TypedEdgeDefinition>> ResolveTypedEdgeClassifiersAsync(
        IEnumerable<InternalId> typeIds) {
        var classifiers = new Dictionary<InternalId, TypedEdgeDefinition>();
        foreach (var typeId in typeIds.Distinct()) {
            var typeState = await graph.Storage.Get(typeId).ConfigureAwait(false);
            if (typeState is null || !graph.SemanticStorage.IsType(typeState))
                continue; // TypeRef is deliberately weak.

            var type = new NodeType(typeState);
            if (TypedEdgeDefinition.TryCreate(graph.GetNodeTypeDefinition(type), out var definition))
                classifiers[type.GlobalId] = definition;
        }

        return classifiers.Values.ToArray();
    }

    private async Task EnsureRelationTypeSatisfiedByAsync(
        NodeBacking relationState,
        NodeType selectedType,
        NodeTypeDefinition selectedDefinition) {
        if (!TypedEdgeDefinition.TryCreate(selectedDefinition, out var definition))
            throw new InvalidOperationException(
                $"Edge type '{selectedType.GlobalId}' must define at least two named members before it can classify a relation.");

        var relation = await graph.SemanticStorage.GetRelationAsync(relationState.GlobalId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Semantic relation '{relationState.GlobalId}' was not found.");
        var otherClassifiers = (await ResolveTypedEdgeClassifiersAsync(relation.TypeIds).ConfigureAwait(false))
            .Where(classifier => classifier.Type.GlobalId != selectedType.GlobalId)
            .Select(classifier => classifier.Type.GlobalId.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (otherClassifiers.Length > 0)
            throw new InvalidOperationException(
                $"Semantic relation '{relation.Id}' already has typed-edge classifier(s) {string.Join(", ", otherClassifiers)}. "
                + "A relation has exactly one concrete Edge classifier; replace the relation explicitly to change its schema.");

        if (relation.Members.Count != definition.Endpoints.Count)
            throw new InvalidOperationException(
                $"Semantic relation '{relation.Id}' has {relation.Members.Count} named members, "
                + $"but Edge type '{selectedType.GlobalId}' defines {definition.Endpoints.Count}.");

        foreach (var endpoint in definition.Endpoints) {
            var matches = relation.Members
                .Where(member => string.Equals(member.Name, endpoint.Name, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Semantic relation '{relation.Id}' must contain exactly one member '{endpoint.Name}', but found {matches.Length}.");

            var member = matches[0];
            if (member.MemberTypeId != endpoint.MemberTypeId)
                throw new InvalidOperationException(
                    $"Member '{endpoint.Name}' of semantic relation '{relation.Id}' has type '{member.MemberTypeId}', "
                    + $"but Edge type '{selectedType.GlobalId}' requires '{endpoint.MemberTypeId}'.");
            var participantIds = member.ParticipantIds.Distinct().ToArray();
            if (participantIds.Length != member.ParticipantIds.Count)
                throw new InvalidOperationException(
                    $"Member '{endpoint.Name}' of semantic relation '{relation.Id}' contains duplicate participants.");
            if (!endpoint.Cardinality.Contains(participantIds.Length))
                throw new InvalidOperationException(
                    $"Member '{endpoint.Name}' of semantic relation '{relation.Id}' expects {endpoint.Cardinality}, "
                    + $"but contains {participantIds.Length} participants.");

            foreach (var participantId in participantIds) {
                var participant = await graph.Storage.Get(participantId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Participant '{participantId}' of semantic relation '{relation.Id}' was not found.");
                if (participant is not ISemanticNodeBacking {
                        SemanticKind: SemanticElementKind.Instance or SemanticElementKind.Relation
                    })
                    throw new InvalidOperationException(
                        $"Participant '{participantId}' of semantic relation '{relation.Id}' is outside the instance graph.");
                if (endpoint.NodeType is not null
                    && !await HasTypeAsync(participant, endpoint.NodeType).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        $"Member '{endpoint.Name}' expects node type '{endpoint.NodeType.GlobalId}', "
                        + $"but participant '{participant.GlobalId}' has another type.");
            }
        }
    }

    private static void EnsureInstanceTypeHasNoRequiredNamedNodeFields(
        NodeType selectedType,
        IReadOnlyCollection<NodeTypeDefinition> definitions) {
        var requiredMembers = definitions
            .SelectMany(static definition => definition.Fields
                .Where(static field => field.ValueKind == NodeFieldValueKind.Node)
                .Where(static field => !field.Cardinality.Contains(0))
                .Select(field => $"{definition.Type.GlobalId}.{field.Name}")
                .Concat(definition.Slots
                    .Where(static slot => !slot.Cardinality.Contains(0))
                    .Select(slot => $"{definition.Type.GlobalId}.{slot.Name}")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (requiredMembers.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Node type '{selectedType.GlobalId}' requires named node fields that are not materialized by this semantic-storage experiment: "
            + $"{string.Join(", ", requiredMembers)}. Model these links as an explicit Edge type and create them with CreateRelation.");
    }

    private async Task EnsureTypedEdgeSatisfiedByAsync(TypedEdgeDefinition definition, IReadOnlyCollection<NodeBacking> endpoints) {
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

    private Task<bool> HasTypeAsync(NodeBacking node, NodeType type) {
        return HasAnyTypeAsync(node, new HashSet<InternalId> { type.GlobalId });
    }

    private async Task<bool> HasAnyTypeAsync(NodeBacking node, IReadOnlySet<InternalId> typeIds) {
        var semanticNode = await ReadSemanticNodeAsync(node).ConfigureAwait(false);
        return semanticNode.AssignedTypes.Any(type => typeIds.Contains(type.GlobalId));
    }

    private sealed record TypedRelationCandidate(
        NodeBacking Backing,
        TypedEdgeDefinition Definition);

}
