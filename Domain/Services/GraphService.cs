using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";
    //TODO Graph
    const string RuntimeTypesVersion = "6";
    public StorageRoot Root { get; }
    public NodeType TypesRoot { get; }
    public NodeType InstancesRoot { get; }
    //TODO Graph

    readonly IGraphStorage storage;
    readonly GraphSearchService searchService;
    readonly ICancellationTokenAccessor cancellationTokens;
    readonly GraphSchemaRegistry schemaRegistry;

    internal GraphService(
        IGraphStorage storage,
        GraphSearchService searchService,
        ICancellationTokenAccessor cancellationTokens,
        GraphSchemaRegistry schemaRegistry
    ) {
        this.storage = storage;
        this.searchService = searchService;
        this.cancellationTokens = cancellationTokens;
        this.schemaRegistry = schemaRegistry;

        Root = new StorageRoot();
        TypesRoot = new NodeType(GraphSystemNodeIds.NodeTypeRoot);
        InstancesRoot = new NodeType(GraphSystemNodeIds.InstanceRoot);
        foreach (var type in schemaRegistry.Types) {
            var node = new NodeType(type.Id);
            TypesRoot.Nodes.Add(node);
            // Edge definitions for CLR types will be handled via SchemaRegistry during runtime
            // We just ensure the node exists in TypesRoot for eager sync.
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var checkResult = await storage.Get(GraphSystemNodeIds.RuntimeTypesInitializer).ConfigureAwait(false);
        if (checkResult.Status != ServiceResultStatus.NotFound)
            throw new Exception($"read initializer marker");

        var storageStateNode = (Node)await GetNodeAsync(GraphSystemNodeIds.RuntimeTypesInitializer);
        storageStateNode.Nodes.Clear();
        storageStateNode.Nodes.Add(new Node(new NodeLocalId(RuntimeTypesVersion)));
        storageStateNode.Nodes.Add(new Node(new NodeLocalId(schemaRegistry.Fingerprint)));
        var result = await AddSubgraph(Root).ConfigureAwait(false);
        if (result.Status != ServiceResultStatus.Ok)
            throw new Exception(result.Error);
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodePath? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNode(localId, path, type?.GlobalId, attributes).ConfigureAwait(false);
    }

    public Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType {
        var typeId = schemaRegistry.GetNodeTypeId(typeof(TNodeType));
        var type = TypesRoot.Nodes.FirstOrDefault(x => x.LocalId == typeId);
        if (type == null)
            return Task.FromResult(ServiceResult<Node>.NotFound());
        return CreateNode(localId, path, type.GlobalId, attributes);
    }

    private async Task<ServiceResult<Node>> CreateNode(
        NodeLocalId localId,
        NodePath? path,
        InternalId? typeId,
        IDictionary<string, string>? attributes) {
        var result = await storage.Create(localId, path, attributes);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToNodeResult(result);

        if (typeId is null)
            return ToNodeResult(result);

        var assign = await AssignNodeTypeAsync(result.Value.GlobalId, typeId).ConfigureAwait(false);
        if (assign.Status != ServiceResultStatus.Ok) {
            await storage.Delete(result.Value.GlobalId).ConfigureAwait(false);
            return ServiceResult<Node>.From(assign);
        }

        var reloaded = await storage.Get(result.Value.GlobalId).ConfigureAwait(false);
        return ToNodeResult(reloaded);
    }

    public async Task<ServiceResult<Node>> GetNodeAsync(NodeRef globalId) {
        var result = await storage.Get(globalId);
        return ToNodeResult(result);
    }

    public async Task<ServiceResult<Node>> GetNeighborNodeAsync(string globalId, string localId) {
        var result = await storage.GetNeighbor(storage.DeserializeGlobalId(globalId), new NodeLocalId(localId));
        return ToNodeResult(result);
    }

    public Task<ServiceResult> UpdateNodeAsync(NodeRef globalId, IDictionary<string, string> attributes) {
        return storage.Update(globalId, attributes);
    }

    public Task<ServiceResult> DeleteNodeAsync(NodeRef globalId) {
        return storage.Delete(globalId);
    }

    public Task<ServiceResult> ConnectNodesAsync(NodeRef sourceGlobalId, NodeRef targetGlobalId) {
        return storage.Connect(sourceGlobalId, targetGlobalId);
    }

    public Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId) where TNodeType : NodeType {
        var type = GetTypeNode<TNodeType>();
        if (type == null)
            return Task.FromResult(ServiceResult<Subgraph>.NotFound());
        return AssignNodeTypeAsync(nodeId, type.GlobalId);
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(NodeRef nodeId, NodeRef typeId) {

        var typeResult = await storage.Get(typeId).ConfigureAwait(false);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);
        var typeNode = await GetTypeNode(typeResult.Value);
        if (typeNode == null)
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not a node type.");

        var nodeResult = await storage.Get(nodeId).ConfigureAwait(false);
        if (nodeResult.Status != ServiceResultStatus.Ok || nodeResult.Value is null)
            return ServiceResult<Subgraph>.From(nodeResult);

        var alreadyAssigned = await IsConnectedAsync(nodeResult.Value, typeNode.GlobalId).ConfigureAwait(false);
        if (alreadyAssigned.Status != ServiceResultStatus.Ok)
            return ServiceResult<Subgraph>.From(alreadyAssigned);

        if (!alreadyAssigned.Value) {
            var connect = await storage.Connect(nodeId, typeId).ConfigureAwait(false);
            if (connect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connect);
        }

        var reloadedNode = await storage.Get(nodeId).ConfigureAwait(false);
        if (reloadedNode.Status != ServiceResultStatus.Ok || reloadedNode.Value is null)
            return ServiceResult<Subgraph>.From(reloadedNode);

        var node2 = new NodeType(typeNode);
        try {
            var definition = schemaRegistry.GetOrBuildDefinition(node2);
            definition.EnsureSatisfiedBy(new InstanceNode(reloadedNode.Value, node2));
        } catch (InvalidOperationException ex) {
            if (!alreadyAssigned.Value)
                await storage.Disconnect(nodeId, typeId).ConfigureAwait(false);
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        return await GetSubgraph([nodeId, typeId], 1).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync(
        NodeRef source,
        NodeRef target,
        NodeRef typeId
    ) {
        var typeResult = await storage.Get(typeId);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);
        var type = typeResult.Value;
        var typeNode = await GetTypeNode(type);
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
        var type = GetTypeNode<TNodeType>();
        if (type == null)
            return ServiceResult<Subgraph>.NotFound();
        var endpointStates = new List<NodeState>(endpointIds.Count);
        foreach (var endpointId in endpointIds) {
            var endpointResult = await storage.Get(endpointId).ConfigureAwait(false);
            if (endpointResult.Status != ServiceResultStatus.Ok || endpointResult.Value is null)
                return ServiceResult<Subgraph>.From(endpointResult);
            endpointStates.Add(endpointResult.Value);
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
        var relationResult = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), GetTypeNodeInternal<InstanceNode>().GlobalId).ConfigureAwait(false);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<Subgraph>.From(relationResult);

        var relation = relationResult.Value;
        var connectType = await storage.Connect(relation.GlobalId, type.GlobalId).ConfigureAwait(false);
        if (connectType.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connectType);

        var ensureEndpointType = GetTypeNodeInternal<EndpointNodeType>();

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstanceResult = await storage.Create(NodeLocalId.Random(), relation.GlobalId).ConfigureAwait(false);
            if (endpointInstanceResult.Status != ServiceResultStatus.Ok || endpointInstanceResult.Value is null)
                return ServiceResult<Subgraph>.From(endpointInstanceResult);

            var endpointInstanceId = endpointInstanceResult.Value.GlobalId;
            var connectEndpointType = await storage.Connect(endpointInstanceId, ensureEndpointType.GlobalId).ConfigureAwait(false);
            if (connectEndpointType.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connectEndpointType);

            var connectEndpoint = await storage.Connect(endpointInstanceId, GetTypeNodeInternal<EndpointNodeType>().GlobalId).ConfigureAwait(false);
            if (connectEndpoint.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connectEndpoint);
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraph(IEnumerable<NodeRef> globalIds, int maxDepth) {

        var roots = new List<NodeState>();
        if (!globalIds.Any())
            roots.AddRange((await storage.Get(storage.Root)).Value!.Nodes);
        else
            foreach (var rootRef in globalIds) {
                var rootsResult = await storage.Get(rootRef);
                if (rootsResult.Status != ServiceResultStatus.Ok || rootsResult.Value is null)
                    return ServiceResult<Subgraph>.From(rootsResult);
                var node = rootsResult.Value;
                if (node.GlobalId != storage.Root)
                    roots.Add(node);
                else
                    roots.AddRange(node.Nodes);
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
            if (result.Status == ServiceResultStatus.NotFound)
                continue;
            if (result.Status != ServiceResultStatus.Ok || result.Value is null)
                return ServiceResult<Subgraph>.From(result);

            var node = new Node(result.Value);
            if (!visitedNodes.Add(node.GlobalId))
                continue;

            nodes[node.GlobalId] = node;
            if (depth >= maxDepth)
                continue;

            var connections = await storage.GetConnectedNodesAsync(result.Value);
            if (connections.Status != ServiceResultStatus.Ok || connections.Value is null)
                return ServiceResult<Subgraph>.From(connections);

            foreach (var neighborId in connections.Value.Select(static x => x.GlobalId))
                if (neighborId != node.GlobalId && discovered.Add(neighborId))
                    queue.Enqueue((neighborId, depth + 1));
        }

        return ServiceResult<Subgraph>.Ok(nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            });
    }

    public async Task<ServiceResult<Subgraph>> AddSubgraph(Node root) {
        NodeState rootState;
        try {
            rootState = root.State;
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        var definition = CollectSubgraph(rootState);
        var persistedNodes = new Dictionary<InternalId, NodeState>();

        foreach (var node in definition.Nodes.Values.OrderBy(static node => node.GlobalId.Count())) {
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var result = await EnsureSubgraphNodeAsync(node).ConfigureAwait(false);
            if (result.Status != ServiceResultStatus.Ok || result.Value is null)
                return ServiceResult<Subgraph>.From(result);

            persistedNodes[result.Value.GlobalId] = result.Value;
        }

        foreach (var edge in definition.Edges) {
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var connect = await storage.Connect(edge.SourceId, edge.TargetId).ConfigureAwait(false);
            if (connect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connect);
        }

        return ServiceResult<Subgraph>.Ok(new Subgraph {
            Nodes = persistedNodes.Values.Select(static node => new Node(node)).ToArray()
        });
    }

    public IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(
        NodeSearchQuery query,
        CancellationToken cancellationToken = default) {
        return searchService.SearchNodesStreamAsync(query, cancellationToken);
    }

    public async Task<ServiceResult<NodeTypeDefinition>> GetNodeTypeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var typeId = schemaRegistry.GetNodeTypeId(typeof(TNodeType));
        var type = TypesRoot.Nodes.FirstOrDefault(x => x.LocalId == typeId);
        if (type is null)
            return ServiceResult<NodeTypeDefinition>.NotFound();
        var typeNode = await GetTypeNode(type);
        if (typeNode == null)
            return ServiceResult<NodeTypeDefinition>.NotFound();
        
        var definition = schemaRegistry.GetOrBuildDefinition(typeNode);
        return ServiceResult<NodeTypeDefinition>.Ok(definition);
    }

    public async Task<ServiceResult<TypedEdgeDefinition>> GetTypedEdgeDefinitionAsync<TNodeType>()
        where TNodeType : NodeType {
        var typeId = schemaRegistry.GetNodeTypeId(typeof(TNodeType));
        var type = TypesRoot.Nodes.FirstOrDefault(x => x.LocalId == typeId);
        if (type is null)
            return ServiceResult<TypedEdgeDefinition>.NotFound();
        var typeNode = await GetTypeNode(type);
        if (typeNode is null)
            return ServiceResult<TypedEdgeDefinition>.NotFound();
            
        var nodeTypeDefinition = schemaRegistry.GetOrBuildDefinition(typeNode);

        return TypedEdgeDefinition.TryCreate(nodeTypeDefinition, out var definition)
            ? ServiceResult<TypedEdgeDefinition>.Ok(definition)
            : ServiceResult<TypedEdgeDefinition>.BadRequest($"CLR type '{typeof(TNodeType).FullName}' does not define a typed edge node type.");
    }

    private static ServiceResult<Node> ToNodeResult(ServiceResult<NodeState> result) {
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<Node>.Ok(new Node(result.Value))
            : ServiceResult<Node>.From(result);
    }

    private async Task<ServiceResult<TypedEdgeShape>> ResolveTypedEdgeShapeAsync(NodeState relation, IReadOnlyCollection<NodeState> connected) {
        var directType = await GetTypeNode(relation);
        if (directType is not null) {
            var portType = GetTypeNodeInternal<PortNodeType>();//TODO сохранять а не искать каждый раз
            var endpointPorts = connected
                .Select(async x => await storage.GetCommonIntersection(x.GlobalId, portType.GlobalId).FirstOrDefaultAsync())
                .Select(x => x?.Result)//TODO нормальное ожидание
                .OfType<NodeState>()
                .ToArray();
            return ServiceResult<TypedEdgeShape>.Ok(new TypedEdgeShape(directType.GlobalId, endpointPorts));
        }

        foreach (var candidatePort in connected) {
            var candidateConnections = await storage.GetConnectedNodesAsync(candidatePort).ConfigureAwait(false);
            if (candidateConnections.Status != ServiceResultStatus.Ok || candidateConnections.Value is null)
                return ServiceResult<TypedEdgeShape>.From(candidateConnections);

            var type = await storage.GetCommonIntersection(candidatePort.GlobalId, TypesRoot.GlobalId).FirstOrDefaultAsync();
            if (type is null)
                continue;

            var endpointPorts = connected
                .Where(node => node.GlobalId != candidatePort.GlobalId)
                //.Where(node => !GraphTypeTopology.IsGraphType(node))
                .ToArray();
            return ServiceResult<TypedEdgeShape>.Ok(new TypedEdgeShape(type.GlobalId, endpointPorts));
        }

        return ServiceResult<TypedEdgeShape>.BadRequest($"Node '{relation.GlobalId}' is not a typed edge node.");
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeRef sourceId, InternalId targetId) {
        var sourceResult = await storage.Get(sourceId);
        if (sourceResult.Status != ServiceResultStatus.Ok || sourceResult.Value is null)
            return ServiceResult.From(sourceResult);

        var connectedResult = await storage.GetConnectedNodesAsync(sourceResult.Value);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult.From(connectedResult);

        return connectedResult.Value.Any(node => node.GlobalId == targetId)
            ? await storage.Disconnect(sourceId, targetId)
            : ServiceResult.Ok();
    }

    private async Task<ServiceResult<bool>> IsConnectedAsync(NodeState node, InternalId targetId) {
        var connectedResult = await storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<bool>.From(connectedResult);

        return ServiceResult<bool>.Ok(connectedResult.Value.Any(neighbor => neighbor.GlobalId == targetId));
    }

    private async Task<ServiceResult<Subgraph>> CreateBasicTypedEdgeSubgraphAsync(NodeRef sourceId, NodeRef targetId, NodeRef typeId) {
        var relationResult = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), typeId);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<Subgraph>.From(relationResult);

        var relation = relationResult.Value;
        var connectType = await storage.Connect(relation.GlobalId, typeId);
        if (connectType.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connectType);

        var sourcePort = await CreatePortAsync(relation.GlobalId, SourcePortRole);
        if (sourcePort.Status != ServiceResultStatus.Ok || sourcePort.Value is null)
            return ServiceResult<Subgraph>.From(sourcePort);
        var targetPort = await CreatePortAsync(relation.GlobalId, TargetPortRole);
        if (targetPort.Status != ServiceResultStatus.Ok || targetPort.Value is null)
            return ServiceResult<Subgraph>.From(targetPort);

        var connect = await storage.Connect(sourcePort.Value.GlobalId, sourceId);
        if (connect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connect);
        connect = await storage.Connect(targetPort.Value.GlobalId, targetId);
        if (connect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connect);

        return await GetSubgraph([relation.GlobalId], 2);
    }

    private async Task<ServiceResult<NodeState>> CreatePortAsync(InternalId relationId, string role) {
        var port = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), relationId).ConfigureAwait(false);
        if (port.Status != ServiceResultStatus.Ok || port.Value is null)
            return port;

        var connectType = await storage.Connect(port.Value.GlobalId, GetTypeNodeInternal<PortNodeType>().GlobalId).ConfigureAwait(false);
        if (connectType.Status != ServiceResultStatus.Ok)
            return new ServiceResult<NodeState>(connectType.Status, Error: connectType.Error);

        return port;
    }

    private async Task<ServiceResult<NodeState>> EnsureSubgraphNodeAsync(NodeState node) => //TODO выглядит как бред
        await EnsureSubgraphNodeAsync(
            node.GlobalId,
            node is VirtualNodeState virtualNode ? virtualNode.AttributeSnapshot() : null).ConfigureAwait(false);

    private async Task<ServiceResult<NodeState>> EnsureSubgraphNodeAsync(
        InternalId id,
        IDictionary<string, string>? attributes) {
        var existing = await storage.Get(id).ConfigureAwait(false);
        if (existing.Status == ServiceResultStatus.Ok && existing.Value is not null)
            return existing;
        if (existing.Status != ServiceResultStatus.NotFound)
            return existing;

        var segments = id.ToArray();
        if (segments.Length == 0)
            return await storage.Get(storage.Root).ConfigureAwait(false);

        InternalId? parentId = null;
        if (segments.Length > 1) {
            parentId = new InternalId(segments.Take(segments.Length - 1));
            var parent = await EnsureSubgraphNodeAsync(parentId, null).ConfigureAwait(false);
            if (parent.Status != ServiceResultStatus.Ok || parent.Value is null)
                return ServiceResult<NodeState>.From(parent);
        }

        var create = await storage.Create(
            segments[^1],
            parentId,
            attributes is null
                ? null
                : new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);

        return create;
    }

    private static SubgraphDefinition CollectSubgraph(NodeState root) {
        var nodes = new Dictionary<InternalId, NodeState>();
        var edges = new HashSet<SubgraphEdge>();
        var queue = new Queue<NodeState>();
        queue.Enqueue(root);

        while (queue.Count > 0) {
            var node = queue.Dequeue();
            if (!nodes.TryAdd(node.GlobalId, node))
                continue;

            if (node is not VirtualNodeState)
                continue;

            foreach (var neighbor in node.Nodes) {
                if (neighbor.GlobalId != node.GlobalId)
                    edges.Add(SubgraphEdge.Create(node.GlobalId, neighbor.GlobalId));
                if (!nodes.ContainsKey(neighbor.GlobalId))
                    queue.Enqueue(neighbor);
            }

            foreach (var edge in node.Edges) {
                if (edge.Node1.GlobalId == edge.Node2.GlobalId)
                    continue;

                edges.Add(SubgraphEdge.Create(edge.Node1.GlobalId, edge.Node2.GlobalId));
                if (!nodes.ContainsKey(edge.Node1.GlobalId))
                    queue.Enqueue(edge.Node1);
                if (!nodes.ContainsKey(edge.Node2.GlobalId))
                    queue.Enqueue(edge.Node2);
            }
        }

        return new SubgraphDefinition(nodes, edges);
    }

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) => new(result.Status, Error: result.Error);

    private sealed record TypedEdgeShape(InternalId TypeId, IReadOnlyCollection<NodeState> EndpointPorts);

    private sealed record SubgraphDefinition(
        IReadOnlyDictionary<InternalId, NodeState> Nodes,
        IReadOnlyCollection<SubgraphEdge> Edges);

    private readonly record struct SubgraphEdge(InternalId SourceId, InternalId TargetId) {
        public static SubgraphEdge Create(InternalId first, InternalId second) =>
            string.CompareOrdinal(first.ToString(), second.ToString()) <= 0
                ? new SubgraphEdge(first, second)
                : new SubgraphEdge(second, first);
    }


    public async Task<NodeType?> GetTypeNode(NodeRef id) {
        var nodeResult = await GetNodeAsync(id);
        if (nodeResult.Value == null)
            return null;
        var node = await GetTypeNode(nodeResult.Value);
        if (node is null)
            return null;
        return node;
    }
    public async Task<NodeType?> GetTypeNode(Node node) {
        var internalState = await GetTypeNode(node.State);
        if (internalState is null)
            return null;
        return new NodeType(internalState);//TODO использовать кэш
    }
    internal async Task<NodeState?> GetTypeNode(NodeState node) {
        var typeNode = node.Nodes
            .Select(async x => await storage.GetCommonIntersection(x.GlobalId, TypesRoot.GlobalId).FirstOrDefaultAsync())
            .FirstOrDefault(x => x is not null)?.Result;//TODO нормальное ожидание
        return typeNode;
    }
    public NodeType? GetTypeNode<T>() where T : NodeType {
        if (typeof(T).Assembly == typeof(Node).Assembly)
            return GetTypeNodeInternal<T>();
        //TODO сделать получение NodeType с NodeState если тип сохранен и null если нет
        throw new NotImplementedException();
    }
    NodeType GetTypeNodeInternal<T>(T? instance = null) where T : NodeType {
        //TODO сделать получение NodeType с NodeState если тип сохранен и null если нет
        throw new NotImplementedException();
    }
}
