using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";

    readonly IGraphStorage storage;
    readonly GraphSearchService searchService;
    readonly GraphSchemaRegistry schemaRegistry;

    internal GraphService(
        IGraphStorage storage,
        GraphSearchService searchService,
        GraphSchemaRegistry schemaRegistry
    ) {
        this.storage = storage;
        this.searchService = searchService;
        this.schemaRegistry = schemaRegistry;
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNodeCore(localId, path, type, attributes).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType {
        var type = await GetRuntimeTypeNodeAsync<TNodeType>().ConfigureAwait(false);
        if (type == null)
            return ServiceResult<Node>.NotFound();
        return await CreateNodeCore(localId, path, type, attributes).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Node>> CreateNodeCore(NodeLocalId localId, NodeRef? parent, NodeType? type, IDictionary<string, string>? attributes) {
        parent = NormalizeParent(parent);
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
        var result = await storage.Get(path);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult<Node>> GetNode(NodeRef path, NodeLocalId localId) {
        var result = await storage.Get(path, localId);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult> UpdateNode(NodeRef path, IDictionary<string, string> attributes) {
        await storage.Update(path, attributes);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteNode(NodeRef globalId) {
        var node = await storage.Get(globalId);
        if (node == null)
            return ServiceResult.NotFound();
        await storage.Delete(node);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ConnectNodesAsync(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        await storage.Connect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }
    public async Task<ServiceResult> Disconnect(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        await storage.Disconnect(sourceGlobalId, targetGlobalId);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId) where TNodeType : NodeType {
        var type = await GetRuntimeTypeNodeAsync<TNodeType>().ConfigureAwait(false);
        if (type == null)
            return ServiceResult<Subgraph>.NotFound();
        return await AssignNodeTypeAsync(nodeId, type.GlobalId).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(NodeRef nodeId, NodeRef typeId) {
        var type = await storage.Get(typeId).ConfigureAwait(false);
        if (type is null)
            return ServiceResult<Subgraph>.NotFound();
        var typeNode = await GetTypeNode(type);
        if (typeNode == null)
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not a node type.");

        var node = await storage.Get(nodeId).ConfigureAwait(false);
        if (node is null)
            return ServiceResult<Subgraph>.NotFound();

        var node2 = new NodeType(typeNode);
        var definition = schemaRegistry.GetOrBuildDefinition(node2, ResolveRuntimeTypeId);
        try {
            definition.EnsureSatisfiedBy(new InstanceNode(node, node2));
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
        var typeResult = await GetNode(typeId);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);
        var type = typeResult.Value;
        var typeNode = await GetTypeNode(type);
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

        return await CreateBasicTypedEdgeSubgraphAsync(source, target, type.GlobalId);
    }

    public Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TNodeType>(NodeRef sourceGlobalId, NodeRef targetGlobalId) where TNodeType : NodeType {
        return ChangeEdgeTypeAsync<TNodeType>([sourceGlobalId, targetGlobalId]);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TNodeType>(IReadOnlyCollection<NodeRef> endpointIds) where TNodeType : NodeType {
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
            definition.EnsureSatisfiedBy(endpointStates.Select(state => new InstanceNode(state, type)).ToArray());
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

        var ensureEndpointType = await GetRequiredRuntimeTypeNodeAsync<EndpointNodeType>().ConfigureAwait(false);

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstance = await storage.Create(
                new NodeLocalId($"endpoint-{endpoint.Name}"),
                relation.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, ensureEndpointType.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, FixedGraphTopology.NodeTypesId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, endpointStates[index].GlobalId).ConfigureAwait(false);
            if (endpoint.NodeTypeId is { } endpointTypeId) {
                var endpointSpec = await storage.Create(
                    new NodeLocalId($"endpoint-spec-{endpoint.Name}"),
                    endpointInstance.GlobalId).ConfigureAwait(false);
                await storage.Connect(endpointSpec.GlobalId, endpointTypeId).ConfigureAwait(false);
            }
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraph(IEnumerable<NodeRef> globalIds, int maxDepth) {

        var requestedIds = globalIds.ToArray();
        var roots = new List<NodeBacking>();
        if (requestedIds.Length == 0)
            roots.AddRange(await GetTopLevelUserRootsAsync(storage.Root).ConfigureAwait(false));
        else
            foreach (var rootRef in requestedIds) {
                var root = await storage.Get(rootRef);
                if (root is null)
                    return ServiceResult<Subgraph>.NotFound();
                if (root.GlobalId != storage.Root.GlobalId)
                    roots.Add(root);
                else
                    roots.AddRange(await GetTopLevelUserRootsAsync(root).ConfigureAwait(false));
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
        var typeNode = await GetRuntimeTypeNodeAsync<TNodeType>().ConfigureAwait(false);
        if (typeNode is null)
            return ServiceResult<NodeTypeDefinition>.NotFound();

        var definition = schemaRegistry.GetOrBuildDefinition(typeNode, ResolveRuntimeTypeId);
        return ServiceResult<NodeTypeDefinition>.Ok(definition);
    }

    public async Task<ServiceResult<TypedEdgeDefinition>> GetTypedEdgeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var typeNode = await GetRuntimeTypeNodeAsync<TNodeType>().ConfigureAwait(false);
        if (typeNode is null)
            return ServiceResult<TypedEdgeDefinition>.NotFound();

        var nodeTypeDefinition = schemaRegistry.GetOrBuildDefinition(typeNode, ResolveRuntimeTypeId);

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

    private async Task<IReadOnlyCollection<NodeBacking>> GetTopLevelUserRootsAsync(NodeBacking root) {
        return (await root.Nodes.ToArrayAsync().ConfigureAwait(false))
            .Where(node => node.GlobalId != FixedGraphTopology.NodeTypesId)
            .Where(node => node.LocalId != FixedGraphTopology.NodeTypesLocalId)
            .ToArray();
    }

    private void AttachInstanceOf(Node instance, NodeType type) {
        if (instance.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Any(incidence => incidence.Type.GlobalId == type.GlobalId))
            return;

        _ = new InstanceOf(new InMemoryEdgeBacking(instance.Backing, type.Backing), instance, type);
    }

    private InternalId ResolveRuntimeTypeId(Type type) {//TODO искать Node а не верить что она есть
        var localId = schemaRegistry.GetNodeTypeId(type);
        return FixedGraphTopology.NodeTypeId(localId);
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeRef sourceId, InternalId targetId) {
        var node = await storage.Get(sourceId);
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Disconnect(sourceId, targetId);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<Subgraph>> CreateBasicTypedEdgeSubgraphAsync(NodeRef sourceId, NodeRef targetId, NodeRef typeId) {
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
        var port = await storage.Create(new NodeLocalId(role), relationId).ConfigureAwait(false);
        var portType = await GetRequiredRuntimeTypeNodeAsync<PortNodeType>().ConfigureAwait(false);
        await storage.Connect(port.GlobalId, portType.GlobalId).ConfigureAwait(false);
        return port;
    }

    private async Task<NodeLocalId> NextAvailableRootLocalIdAsync(string prefix) {
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

    private sealed record TypedEdgeShape(InternalId TypeId, IReadOnlyCollection<NodeBacking> EndpointPorts);

    private sealed record SubgraphDefinition(
        IReadOnlyCollection<SubgraphNode> Nodes,
        IReadOnlyCollection<SubgraphEdge> Edges);

    private sealed record SubgraphNode(NodeBacking Node, NodePath Path, NodePath? ParentPath);

    private readonly record struct SubgraphEdge(NodePath SourceId, NodePath TargetId);


    public async Task<NodeType?> GetTypeNode(NodeRef id) {
        var nodeResult = await GetNode(id);
        if (nodeResult.Value == null)
            return null;
        var node = await GetTypeNode(nodeResult.Value);
        if (node is null)
            return null;
        return node;
    }
    public async Task<NodeType?> GetTypeNode(Node node) {
        var internalState = await GetTypeNode(node.Backing);
        if (internalState is null)
            return null;
        return new NodeType(internalState);
    }
    internal async Task<NodeBacking?> GetTypeNode(NodeBacking node) {
        if (node.GlobalId == FixedGraphTopology.NodeTypesId)
            return null;
        if (await node.Nodes.AnyAsync(x => x.GlobalId == FixedGraphTopology.NodeTypesId))
            return node;
        var typeNode = await node.Nodes.SelectMany(x => x.Nodes).FirstOrDefaultAsync(x => x.GlobalId == FixedGraphTopology.NodeTypesId);
        return typeNode;
    }
    private async Task<NodeType?> GetRuntimeTypeNodeAsync<T>() where T : NodeType {
        var typeId = schemaRegistry.GetNodeTypeId(typeof(T));
        var state = await storage.Get(FixedGraphTopology.NodeTypePath(typeId)).ConfigureAwait(false);
        return state is null
            ? null
            : new NodeType(state);
    }

    private async Task<NodeType> GetRequiredRuntimeTypeNodeAsync<T>() where T : NodeType {
        return await GetRuntimeTypeNodeAsync<T>().ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Runtime graph type '{typeof(T).FullName}' is not present in the graph service type root.");
    }
}
