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

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNodeCore(graph, localId, path, type, attributes).ConfigureAwait(false);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> CreateNodeType(
        NodeLocalId localId,
        bool isAbstract = false,
        IEnumerable<NodeFieldDefinition>? fields = null,
        IEnumerable<NodeSlotDefinition>? slots = null,
        IEnumerable<NodeType>? requiredTypes = null) {
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
        }

        var storage = graph.Storage;
        var existing = await storage.Get(graph.NodeTypes.GlobalId, localId).ConfigureAwait(false);
        if (existing is not null)
            return ServiceResult<NodeTypeDefinition>.Conflict($"Node type '{localId}' already exists.");

        NodeBacking? created = null;
        var createdRelations = new List<NodeBacking>();
        try {
            created = await storage.Create(localId, graph.NodeTypes.GlobalId).ConfigureAwait(false);
            var type = new NodeType(created);
            var definition = CreateNodeTypeDefinition(type, isAbstract, fieldArray, slotArray, requiredTypeArray);
            await DynamicNodeTypeDefinitionStorage.WriteAsync(storage, definition).ConfigureAwait(false);
            graph.RegisterNodeTypeDefinition(definition);
            foreach (var requiredType in requiredTypeArray)
                createdRelations.Add(await CreateRequiresRelationAsync(type, requiredType).ConfigureAwait(false));

            return ServiceResult<NodeTypeDefinition>.Ok(graph.GetNodeTypeDefinition(type));
        } catch (Exception ex) {
            foreach (var relation in createdRelations)
                await storage.Delete(relation).ConfigureAwait(false);
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

        var semanticRelations = await FindIncidentTypedRelationsAsync(node).ConfigureAwait(false);
        foreach (var relation in semanticRelations)
            await storage.Delete(relation).ConfigureAwait(false);

        // An endpoint instance is owned by its relation carrier. Deleting the
        // carrier above also deletes the endpoint hierarchy child, so reload
        // before attempting to delete the originally requested node.
        var remainingNode = await storage.Get(node.GlobalId).ConfigureAwait(false);
        if (remainingNode is not null)
            await storage.Delete(remainingNode).ConfigureAwait(false);
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
        if (graph.GetNodeTypeDefinition(typeNode).IsAbstract)
            return ServiceResult<Subgraph>.BadRequest($"Abstract graph type '{typeNode.GlobalId}' cannot be assigned directly.");

        var node = await storage.Get(nodeId).ConfigureAwait(false);
        if (node is null)
            return ServiceResult<Subgraph>.NotFound();

        IReadOnlyCollection<NodeType> effectiveTypes;
        try {
            effectiveTypes = GetRequiredTypeClosure(typeNode);
            var effectiveDefinitions = effectiveTypes
                .Select(graph.GetNodeTypeDefinition)
                .ToArray();
            EnsureLegacyMembersAreUnambiguous(effectiveDefinitions);
            foreach (var definition in effectiveDefinitions)
                await EnsureNodeTypeSatisfiedByAsync(definition, node).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        var createdRelations = new List<NodeBacking>();
        var createdCompatibilityLinks = new List<NodeType>();
        var relationRoots = new Dictionary<InternalId, NodeBacking>();
        try {
            foreach (var effectiveType in effectiveTypes) {
                var existingRelations = await FindTypeInstanceRelationsAsync(node, effectiveType).ConfigureAwait(false);
                if (existingRelations.Count > 1)
                    throw new InvalidOperationException(
                        $"Node '{node.GlobalId}' has more than one materialized instance of type '{effectiveType.GlobalId}'.");

                var relation = existingRelations.SingleOrDefault();
                if (relation is null) {
                    relation = await CreateTypeInstanceRelationAsync(node, effectiveType).ConfigureAwait(false);
                    createdRelations.Add(relation);
                }
                relationRoots[relation.GlobalId] = relation;

                // Transitional compatibility index. Semantic reads prefer the
                // materialized InstanceOf relation and only fall back to this edge.
                if (!await node.Nodes.AnyAsync(neighbor => neighbor.GlobalId == effectiveType.GlobalId).ConfigureAwait(false)) {
                    await storage.Connect(node.GlobalId, effectiveType.GlobalId).ConfigureAwait(false);
                    createdCompatibilityLinks.Add(effectiveType);
                }
            }

            return await GetSubgraph(
                new NodeRef[] { node.GlobalId }.Concat(relationRoots.Keys),
                2).ConfigureAwait(false);
        } catch (Exception ex) {
            foreach (var relation in createdRelations)
                await storage.Delete(relation).ConfigureAwait(false);
            foreach (var compatibilityType in createdCompatibilityLinks)
                await storage.Disconnect(node.GlobalId, compatibilityType.GlobalId).ConfigureAwait(false);
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
            var relations = await FindRequiresRelationsAsync(derivedType, requiredType).ConfigureAwait(false);
            if (relations.Count > 1)
                throw new InvalidOperationException(
                    $"Type '{derivedType.GlobalId}' requires type '{requiredType.GlobalId}' more than once.");

            var relation = relations.SingleOrDefault()
                ?? await CreateRequiresRelationAsync(derivedType, requiredType).ConfigureAwait(false);
            return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }
    }

    public async Task<ServiceResult<InstanceNode>> GetSemanticNodeAsync(
        NodeRef nodeId,
        IEnumerable<NodeRef>? basisTypeIds = null) {
        var state = await graph.Storage.Get(nodeId).ConfigureAwait(false);
        if (state is null)
            return ServiceResult<InstanceNode>.NotFound();

        try {
            var basis = await ResolveTypeBasisAsync(basisTypeIds).ConfigureAwait(false);
            return ServiceResult<InstanceNode>.Ok(
                await ReadSemanticNodeAsync(state, basis).ConfigureAwait(false));
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

    public async Task<ServiceResult<TypedEdgeInstance>> GetTypedEdgeInstanceAsync(
        NodeRef relationId,
        IEnumerable<NodeRef>? basisTypeIds = null) {
        var relation = await graph.Storage.Get(relationId).ConfigureAwait(false);
        if (relation is null)
            return ServiceResult<TypedEdgeInstance>.NotFound();

        try {
            var basis = await ResolveTypeBasisAsync(basisTypeIds).ConfigureAwait(false);
            var classifiers = new List<NodeType>();
            await foreach (var neighbor in relation.Nodes.ConfigureAwait(false)) {
                var type = await AsBasisTypeAsync(neighbor, basis).ConfigureAwait(false);
                if (type is null)
                    continue;
                if (TypedEdgeDefinition.TryCreate(graph.GetNodeTypeDefinition(type), out _))
                    classifiers.Add(type);
            }

            var classifier = classifiers
                .DistinctBy(static type => type.GlobalId)
                .Single();
            var definition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(classifier));
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

    private async Task<InstanceNode> ReadSemanticNodeAsync(
        NodeBacking state,
        IReadOnlyDictionary<InternalId, NodeType>? basis = null) {
        var typeInstances = new Dictionary<InternalId, NodeTypeInstance>();
        var instanceOfType = graph.GetRequiredRuntimeType<InstanceOfConnectionNodeType>();
        var instanceOfDefinition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(instanceOfType));
        var materializedRelations = await TypedEdgeSubgraphCodec.FindIncidentRelationsAsync(
            state,
            instanceOfDefinition,
            instanceOfDefinition.Endpoints.Single(endpoint =>
                endpoint.Name == nameof(InstanceOfConnectionNodeType.Instance))).ConfigureAwait(false);

        foreach (var relation in materializedRelations) {
            var typedEdge = await TypedEdgeSubgraphCodec.ReadAsync(
                graph,
                relation,
                instanceOfDefinition).ConfigureAwait(false);
            var typeParticipant = typedEdge.Endpoint(nameof(InstanceOfConnectionNodeType.Type)).Participant;
            var type = await AsBasisTypeAsync(typeParticipant.Backing, basis).ConfigureAwait(false);
            if (type is null)
                continue;
            if (typeInstances.ContainsKey(type.GlobalId))
                throw new InvalidOperationException(
                    $"Node '{state.GlobalId}' has more than one materialized instance of type '{type.GlobalId}'.");
            typeInstances[type.GlobalId] = new NodeTypeInstance(type, typedEdge.Relation);
        }

        // Read old data and the temporary UI compatibility index. A materialized
        // witness always wins when both representations are present.
        await foreach (var neighbor in state.Nodes.ConfigureAwait(false)) {
            if (TypedEdgeSubgraphCodec.IsDirectChildOf(neighbor.GlobalId, state.GlobalId)
                || TypedEdgeSubgraphCodec.IsDirectChildOf(state.GlobalId, neighbor.GlobalId))
                continue;
            var type = await AsBasisTypeAsync(neighbor, basis).ConfigureAwait(false);
            if (type is not null && !typeInstances.ContainsKey(type.GlobalId))
                typeInstances[type.GlobalId] = new NodeTypeInstance(type);
        }

        var assignedTypeIds = typeInstances.Keys.ToHashSet();
        foreach (var typeInstance in typeInstances.Values) {
            var missingRequiredTypes = GetRequiredTypeClosure(typeInstance.Type)
                .Skip(1)
                .Where(requiredType => !assignedTypeIds.Contains(requiredType.GlobalId))
                .Select(static requiredType => requiredType.GlobalId.ToString())
                .ToArray();
            if (missingRequiredTypes.Length > 0)
                throw new InvalidOperationException(
                    $"Node '{state.GlobalId}' implements '{typeInstance.Type.GlobalId}' but is missing materialized required types: {string.Join(", ", missingRequiredTypes)}.");
        }

        return new InstanceNode(state, typeInstances.Values);
    }

    private async Task<IReadOnlyDictionary<InternalId, NodeType>?> ResolveTypeBasisAsync(
        IEnumerable<NodeRef>? basisTypeIds) {
        if (basisTypeIds is null)
            return null;

        var result = new Dictionary<InternalId, NodeType>();
        foreach (var typeId in basisTypeIds) {
            var state = await graph.Storage.Get(typeId).ConfigureAwait(false);
            if (state is null)
                throw new InvalidOperationException($"Basis type node '{typeId}' was not found.");
            result[state.GlobalId] = new NodeType(state);
        }
        return result;
    }

    private async Task<NodeType?> AsBasisTypeAsync(
        NodeBacking state,
        IReadOnlyDictionary<InternalId, NodeType>? basis) {
        if (basis is not null)
            return basis.TryGetValue(state.GlobalId, out var type) ? type : null;
        return await graph.AsNodeTypeAsync(state).ConfigureAwait(false);
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

    private async Task<IReadOnlyCollection<NodeBacking>> FindTypeInstanceRelationsAsync(
        NodeBacking instance,
        NodeType type) {
        var instanceOfType = graph.GetRequiredRuntimeType<InstanceOfConnectionNodeType>();
        var definition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(instanceOfType));
        var relations = await TypedEdgeSubgraphCodec.FindIncidentRelationsAsync(
            instance,
            definition,
            definition.Endpoints.Single(endpoint =>
                endpoint.Name == nameof(InstanceOfConnectionNodeType.Instance))).ConfigureAwait(false);
        var matches = new List<NodeBacking>();
        foreach (var relation in relations) {
            var typedEdge = await TypedEdgeSubgraphCodec.ReadAsync(graph, relation, definition).ConfigureAwait(false);
            if (typedEdge.Endpoint(nameof(InstanceOfConnectionNodeType.Type)).Participant.GlobalId == type.GlobalId)
                matches.Add(relation);
        }
        return matches;
    }

    private async Task<NodeBacking> CreateTypeInstanceRelationAsync(NodeBacking instance, NodeType type) {
        var instanceOfType = graph.GetRequiredRuntimeType<InstanceOfConnectionNodeType>();
        var definition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(instanceOfType));
        return await TypedEdgeSubgraphCodec.CreateAsync(
            graph.Storage,
            await NextAvailableRootLocalIdAsync(RelationPrefix(instanceOfType.LocalId)).ConfigureAwait(false),
            definition,
            new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal) {
                [nameof(InstanceOfConnectionNodeType.Instance)] = [instance],
                [nameof(InstanceOfConnectionNodeType.Type)] = [type.Backing]
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<NodeBacking>> FindRequiresRelationsAsync(
        NodeType derivedType,
        NodeType requiredType) {
        var requiresType = graph.GetRequiredRuntimeType<RequiresConnectionNodeType>();
        var definition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(requiresType));
        var relations = await TypedEdgeSubgraphCodec.FindIncidentRelationsAsync(
            derivedType.Backing,
            definition,
            definition.Endpoints.Single(endpoint =>
                endpoint.Name == nameof(RequiresConnectionNodeType.Derived))).ConfigureAwait(false);
        var matches = new List<NodeBacking>();
        foreach (var relation in relations) {
            var typedEdge = await TypedEdgeSubgraphCodec.ReadAsync(graph, relation, definition).ConfigureAwait(false);
            if (typedEdge.Endpoint(nameof(RequiresConnectionNodeType.Required)).Participant.GlobalId == requiredType.GlobalId)
                matches.Add(relation);
        }
        return matches;
    }

    private async Task<IReadOnlyCollection<NodeBacking>> FindIncidentTypedRelationsAsync(
        NodeBacking participant) {
        var definitionsResult = await GetTypeDefinitionsAsync().ConfigureAwait(false);
        if (definitionsResult.Status != ServiceResultStatus.Ok || definitionsResult.Value is null)
            throw new InvalidOperationException(definitionsResult.Error ?? "Graph type catalog cannot be read.");

        var typedDefinitions = definitionsResult.Value
            .Select(definition => TypedEdgeDefinition.TryCreate(definition, out var typedEdge)
                ? typedEdge
                : null)
            .OfType<TypedEdgeDefinition>()
            .ToArray();
        var relations = new Dictionary<InternalId, NodeBacking>();
        var participantSegments = participant.GlobalId.ToArray();
        if (participantSegments.Length > 1) {
            var parent = await graph.Storage.Get(
                new NodePath(participantSegments.Take(participantSegments.Length - 1))).ConfigureAwait(false);
            if (parent is not null) {
                foreach (var definition in typedDefinitions) {
                    var isEndpointOccurrence = false;
                    foreach (var endpoint in definition.Endpoints) {
                        if (!await TypedEdgeSubgraphCodec.HasMemberClassifierAsync(
                                participant,
                                endpoint.MemberTypeId).ConfigureAwait(false))
                            continue;
                        isEndpointOccurrence = true;
                        break;
                    }
                    if (!isEndpointOccurrence)
                        continue;
                    if (await TypedEdgeSubgraphCodec.HasClassifierAsync(
                            parent,
                            definition.Type.GlobalId).ConfigureAwait(false))
                        relations[parent.GlobalId] = parent;
                }
            }
        }

        foreach (var definition in typedDefinitions) {
            foreach (var endpoint in definition.Endpoints) {
                var incident = await TypedEdgeSubgraphCodec.FindIncidentRelationsAsync(
                    participant,
                    definition,
                    endpoint).ConfigureAwait(false);
                foreach (var relation in incident)
                    relations[relation.GlobalId] = relation;
            }
        }

        var classifier = await graph.AsNodeTypeAsync(participant).ConfigureAwait(false);
        if (classifier is not null
            && TypedEdgeDefinition.TryCreate(graph.GetNodeTypeDefinition(classifier), out var classifierDefinition)) {
            await foreach (var neighbor in participant.Nodes.ConfigureAwait(false)) {
                if (!await TypedEdgeSubgraphCodec.HasClassifierAsync(neighbor, classifier.GlobalId).ConfigureAwait(false))
                    continue;
                var neighborNodes = await neighbor.Nodes.ToArrayAsync().ConfigureAwait(false);
                var hasEndpointOccurrence = false;
                foreach (var endpointNode in neighborNodes.Where(node =>
                             TypedEdgeSubgraphCodec.IsDirectChildOf(node.GlobalId, neighbor.GlobalId))) {
                    foreach (var endpoint in classifierDefinition.Endpoints) {
                        if (!await TypedEdgeSubgraphCodec.HasMemberClassifierAsync(
                                endpointNode,
                                endpoint.MemberTypeId).ConfigureAwait(false))
                            continue;
                        hasEndpointOccurrence = true;
                        break;
                    }
                    if (hasEndpointOccurrence)
                        break;
                }
                if (hasEndpointOccurrence)
                    relations[neighbor.GlobalId] = neighbor;
            }
        }

        return relations.Values.ToArray();
    }

    private async Task<IReadOnlyCollection<NodeBacking>> FindTypedRelationsForParticipantsAsync(
        IReadOnlyCollection<NodeBacking> participants) {
        if (participants.Count == 0)
            return Array.Empty<NodeBacking>();

        var definitionsResult = await GetTypeDefinitionsAsync().ConfigureAwait(false);
        if (definitionsResult.Status != ServiceResultStatus.Ok || definitionsResult.Value is null)
            throw new InvalidOperationException(definitionsResult.Error ?? "Graph type catalog cannot be read.");
        var definitions = definitionsResult.Value
            .Select(definition => TypedEdgeDefinition.TryCreate(definition, out var typedEdge)
                ? typedEdge
                : null)
            .OfType<TypedEdgeDefinition>()
            .ToArray();
        var expectedParticipantIds = participants
            .Select(static participant => participant.GlobalId.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var matches = new Dictionary<InternalId, NodeBacking>();
        var incidentRelations = await FindIncidentTypedRelationsAsync(participants.First()).ConfigureAwait(false);
        foreach (var relation in incidentRelations) {
            var classifiers = new List<TypedEdgeDefinition>();
            foreach (var definition in definitions)
                if (await TypedEdgeSubgraphCodec.HasClassifierAsync(
                        relation,
                        definition.Type.GlobalId).ConfigureAwait(false))
                    classifiers.Add(definition);

            if (classifiers.Count != 1)
                throw new InvalidOperationException(
                    $"Typed edge '{relation.GlobalId}' must have exactly one typed-edge classifier, but found {classifiers.Count}.");

            var typedEdge = await TypedEdgeSubgraphCodec.ReadAsync(
                graph,
                relation,
                classifiers[0]).ConfigureAwait(false);
            var actualParticipantIds = typedEdge.Endpoints
                .SelectMany(static endpoint => endpoint.Participants)
                .Select(static participant => participant.GlobalId.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (expectedParticipantIds.SequenceEqual(actualParticipantIds, StringComparer.Ordinal))
                matches[relation.GlobalId] = relation;
        }

        return matches.Values.ToArray();
    }

    private async Task<NodeBacking> CreateRequiresRelationAsync(NodeType derivedType, NodeType requiredType) {
        var requiresType = graph.GetRequiredRuntimeType<RequiresConnectionNodeType>();
        var definition = TypedEdgeDefinition.Create(graph.GetNodeTypeDefinition(requiresType));
        return await TypedEdgeSubgraphCodec.CreateAsync(
            graph.Storage,
            await NextAvailableRootLocalIdAsync(RelationPrefix(requiresType.LocalId)).ConfigureAwait(false),
            definition,
            new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal) {
                [nameof(RequiresConnectionNodeType.Derived)] = [derivedType.Backing],
                [nameof(RequiresConnectionNodeType.Required)] = [requiredType.Backing]
            }).ConfigureAwait(false);
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
            await NextAvailableRootLocalIdAsync(RelationPrefix(definition.Type.LocalId)).ConfigureAwait(false),
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
                    $"Typed edge was not replaced because the previous carrier could not be removed: {ex.Message}");
            }
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
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
        IReadOnlyCollection<NodeSlotDefinition> slots,
        IReadOnlyCollection<NodeType> requiredTypes) {
        return new NodeTypeDefinition(
            type,
            isAbstract,
            slots.Concat(fields.Select(static field => field.ToSlotDefinition()).OfType<NodeSlotDefinition>()).ToArray(),
            fields.ToArray()) {
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
        var localId = node.LocalId.ToString();
        return ReservedDynamicTypeChildNames.Contains(localId);
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

    private static void EnsureLegacyMembersAreUnambiguous(
        IReadOnlyCollection<NodeTypeDefinition> definitions) {
        var members = new List<LegacyNodeMember>();
        foreach (var definition in definitions) {
            foreach (var slotGroup in definition.Slots.GroupBy(static slot => slot.Name, StringComparer.OrdinalIgnoreCase)) {
                members.Add(new LegacyNodeMember(
                    $"{definition.Type.GlobalId}.{slotGroup.Key}",
                    IsWildcard: false,
                    slotGroup.SelectMany(static slot => slot.AllowedTypes)
                        .Select(static type => type.GlobalId)
                        .ToHashSet()));
            }
        }

        for (var leftIndex = 0; leftIndex < members.Count; leftIndex++) {
            for (var rightIndex = leftIndex + 1; rightIndex < members.Count; rightIndex++) {
                var left = members[leftIndex];
                var right = members[rightIndex];
                if (!left.IsWildcard
                    && !right.IsWildcard
                    && !left.AllowedTypeIds.Overlaps(right.AllowedTypeIds))
                    continue;

                throw new InvalidOperationException(
                    $"Node members '{left.QualifiedName}' and '{right.QualifiedName}' cannot be resolved from unnamed raw neighbors. Materialized member instances are required.");
            }
        }
    }

    private async Task EnsureSlotSatisfiedByAsync(NodeSlotDefinition slot, NodeType instanceType, NodeBacking instance) {
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

    private sealed record LegacyNodeMember(
        string QualifiedName,
        bool IsWildcard,
        HashSet<InternalId> AllowedTypeIds);

}
