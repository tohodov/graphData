using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";

    readonly GraphProvider? graphProvider;
    readonly Graph? graph;
    readonly GraphSearchService searchService;

    internal GraphService(
        GraphProvider graphProvider,
        GraphSearchService searchService
    ) {
        this.graphProvider = graphProvider;
        this.searchService = searchService;
    }

    internal GraphService(
        Graph graph,
        GraphSearchService searchService
    ) {
        this.graph = graph;
        this.searchService = searchService;
    }

    internal GraphService(
        IGraphStorage storage,
        GraphSearchService searchService,
        GraphSchemaRegistry schemaRegistry
    ) : this(new GraphProvider(storage, schemaRegistry), searchService) {
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        var graph = await GetGraphAsync().ConfigureAwait(false);
        return await CreateNodeCore(graph, localId, path, type, attributes).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType {
        var graph = await GetGraphAsync().ConfigureAwait(false);
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
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        var result = await storage.Get(path);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult<Node>> GetNode(NodeRef path, NodeLocalId localId) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        var result = await storage.Get(path, localId);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult> UpdateNode(NodeRef path, IDictionary<string, string> attributes) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        await storage.Update(path, attributes);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteNode(NodeRef globalId) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        var node = await storage.Get(globalId);
        if (node == null)
            return ServiceResult.NotFound();
        await storage.Delete(node);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ConnectNodesAsync(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        await storage.Connect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }
    public async Task<ServiceResult> Disconnect(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        await storage.Disconnect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId) where TNodeType : NodeType {
        var graph = await GetGraphAsync().ConfigureAwait(false);
        var type = graph.GetRuntimeType<TNodeType>();
        if (type == null)
            return ServiceResult<Subgraph>.NotFound();
        return await AssignNodeTypeAsync(nodeId, type.GlobalId).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(NodeRef nodeId, NodeRef typeId) {
        var graph = await GetGraphAsync().ConfigureAwait(false);
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
        var graph = await GetGraphAsync().ConfigureAwait(false);
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
        var graph = await GetGraphAsync().ConfigureAwait(false);
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
        var graph = await GetGraphAsync().ConfigureAwait(false);
        var storage = graph.Storage;

        var requestedIds = globalIds.ToArray();
        var roots = new List<NodeBacking>();
        if (requestedIds.Length == 0)
            roots.AddRange(await graph.GetTopLevelUserRootStatesAsync().ConfigureAwait(false));
        else
            foreach (var rootRef in requestedIds) {
                var root = await storage.Get(rootRef);
                if (root is null)
                    return ServiceResult<Subgraph>.NotFound();
                if (root.GlobalId != storage.Root.GlobalId)
                    roots.Add(root);
                else
                    roots.AddRange(await graph.GetTopLevelUserRootStatesAsync().ConfigureAwait(false));
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

    public IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(
        NodeSearchQuery query,
        CancellationToken cancellationToken = default) {
        return searchService.SearchNodesStreamAsync(query, cancellationToken);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> GetNodeTypeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var graph = await GetGraphAsync().ConfigureAwait(false);
        var typeNode = graph.GetRuntimeType<TNodeType>();
        if (typeNode is null)
            return ServiceResult<NodeTypeDefinition>.NotFound();

        var definition = graph.GetNodeTypeDefinition(typeNode);
        return ServiceResult<NodeTypeDefinition>.Ok(definition);
    }

    public async Task<ServiceResult<TypedEdgeDefinition>> GetTypedEdgeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var graph = await GetGraphAsync().ConfigureAwait(false);
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
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
        var node = await storage.Get(sourceId);
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Disconnect(sourceId, targetId);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<Subgraph>> CreateBasicTypedEdgeSubgraphAsync(NodeRef sourceId, NodeRef targetId, NodeRef typeId) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
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
        var graph = await GetGraphAsync().ConfigureAwait(false);
        var storage = graph.Storage;
        var port = await storage.Create(new NodeLocalId(role), relationId).ConfigureAwait(false);
        var portType = graph.GetRequiredRuntimeType<PortNodeType>();
        await storage.Connect(port.GlobalId, portType.GlobalId).ConfigureAwait(false);
        return port;
    }

    private async Task<NodeLocalId> NextAvailableRootLocalIdAsync(string prefix) {
        var storage = (await GetGraphAsync().ConfigureAwait(false)).Storage;
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

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) => new(result.Status, Error: result.Error);

    public async Task<NodeType?> GetTypeNode(NodeRef id) {
        var graph = await GetGraphAsync().ConfigureAwait(false);
        var state = await graph.Storage.Get(id).ConfigureAwait(false);
        if (state is null)
            return null;
        return await graph.AsNodeTypeAsync(state).ConfigureAwait(false);
    }

    public async Task<NodeType?> GetTypeNode(Node node) {
        var graph = await GetGraphAsync().ConfigureAwait(false);
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

    private ValueTask<Graph> GetGraphAsync() {
        if (graph is not null)
            return ValueTask.FromResult(graph);

        return graphProvider!.GetGraphAsync();
    }
}
