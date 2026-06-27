using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";
    //TODO Graph
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
        var graphData = new Node("graphdata");
        Root.Nodes.Add(graphData);
        var types = new Node("types");
        graphData.Nodes.Add(types);
        TypesRoot = new NodeType("nodes");
        types.Nodes.Add(TypesRoot);
        InstancesRoot = new NodeType("instances");
        graphData.Nodes.Add(InstancesRoot);
        foreach (var type in schemaRegistry.Types)
            TypesRoot.Nodes.Add(new NodeType(type.Id));
    }
    public async Task<ServiceResult<Node>> CreateNode(NodeRef node, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        if (node is NodePath path) {
            var localId = path.Last();
            return await CreateNodeCore(localId, new NodePath(path.Except([localId])), type, attributes).ConfigureAwait(false);
        } else if (node is InternalId id) {
            var localId = id.Last();
            return await CreateNodeCore(localId, new InternalId(id.Except([localId])), type, attributes).ConfigureAwait(false);
        } else throw new NotImplementedException();//TODO надо переобдумать контракт GraphService
    }
    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodeRef? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNodeCore(localId, path, type, attributes).ConfigureAwait(false);
    }

    public Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType {
        var typeId = schemaRegistry.GetNodeTypeId(typeof(TNodeType));
        var type = TypesRoot.Nodes.OfType<NodeType>().FirstOrDefault(x => x.LocalId == typeId);
        if (type == null)
            return Task.FromResult(ServiceResult<Node>.NotFound());
        return CreateNodeCore(localId, path, type, attributes);
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

    public async Task<ServiceResult<Node>> GetNodeAsync(NodeRef globalId) {
        var result = await storage.Get(globalId);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult<Node>> GetNeighborNodeAsync(InternalId internalId, NodeLocalId localId) {
        var result = await storage.Get(internalId, localId);
        if (result is null)
            return ServiceResult<Node>.NotFound();
        return new Node(result);
    }

    public async Task<ServiceResult> UpdateNodeAsync(NodeRef globalId, IDictionary<string, string> attributes) {
        await storage.Update(globalId, attributes);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteNodeAsync(NodeRef globalId) {
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

    public Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId) where TNodeType : NodeType {
        var type = GetTypeNode<TNodeType>();
        if (type == null)
            return Task.FromResult(ServiceResult<Subgraph>.NotFound());
        return AssignNodeTypeAsync(nodeId, type.GlobalId);
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

        var alreadyAssigned = await node.Nodes.AnyAsync(neighbor => neighbor.GlobalId == typeNode.GlobalId);
        if (!alreadyAssigned)
            await storage.Connect(nodeId, typeId).ConfigureAwait(false);

        var reloadedNode = await storage.Get(nodeId).ConfigureAwait(false);
        if (reloadedNode is null)
            return ServiceResult<Subgraph>.NotFound();

        var node2 = new NodeType(typeNode);
        var definition = schemaRegistry.GetOrBuildDefinition(node2);
        definition.EnsureSatisfiedBy(new InstanceNode(reloadedNode, node2));
        return await GetSubgraph([nodeId, typeId], 1).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync(
        NodeRef source,
        NodeRef target,
        NodeRef typeId
    ) {
        var typeResult = await GetNodeAsync(typeId);
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
        var relation = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), GetTypeNodeInternal<InstanceNode>().GlobalId).ConfigureAwait(false);
        await storage.Connect(relation.GlobalId, type.GlobalId).ConfigureAwait(false);

        var ensureEndpointType = GetTypeNodeInternal<EndpointNodeType>();

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstance = await storage.Create(NodeLocalId.Random(), relation.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, ensureEndpointType.GlobalId).ConfigureAwait(false);
            await storage.Connect(endpointInstance.GlobalId, GetTypeNodeInternal<EndpointNodeType>().GlobalId).ConfigureAwait(false);
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraph(IEnumerable<NodeRef> globalIds, int maxDepth) {

        var roots = new List<NodeBacking>();
        if (!globalIds.Any())
            roots.AddRange(await storage.Root.Nodes.ToArrayAsync());
        else
            foreach (var rootRef in globalIds) {
                var root = await storage.Get(rootRef);
                if (root is null)
                    return ServiceResult<Subgraph>.NotFound();
                if (root.GlobalId != storage.Root.GlobalId)
                    roots.Add(root);
                else
                    roots.AddRange(await root.Nodes.ToArrayAsync());
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

    public async Task<ServiceResult<Subgraph>> AddSubgraph(Node root) {
        var definition = CollectSubgraph(root);
        var persistedNodes = new List<NodeBacking>();

        foreach (var node in definition.Nodes) {
            cancellationTokens.Token.ThrowIfCancellationRequested();
            var existing = !node.Path.Any()
                ? storage.Root
                : await storage.Get(node.Path).ConfigureAwait(false);
            if (existing is null)
                existing = await storage.Create(node.Node.LocalId, node.ParentPath).ConfigureAwait(false);
            node.Node.Backing = existing;
            persistedNodes.Add(existing);
        }

        foreach (var edge in definition.Edges) {
            cancellationTokens.Token.ThrowIfCancellationRequested();
            await storage.Connect(edge.SourceId, edge.TargetId).ConfigureAwait(false);
        }

        return ServiceResult<Subgraph>.Ok(new Subgraph {
            Nodes = persistedNodes.Select(static node => new Node(node)).ToArray()
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

        _ = new InstanceOf(new EdgeStateReferenced(instance.Backing, type.Backing), instance, type);
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeRef sourceId, InternalId targetId) {
        var node = await storage.Get(sourceId);
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Disconnect(sourceId, targetId);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<Subgraph>> CreateBasicTypedEdgeSubgraphAsync(NodeRef sourceId, NodeRef targetId, NodeRef typeId) {
        var relation = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), typeId);
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
        var port = await storage.Create(new NodeLocalId(Guid.NewGuid().ToString()), relationId).ConfigureAwait(false);
        await storage.Connect(port.GlobalId, GetTypeNodeInternal<PortNodeType>().GlobalId).ConfigureAwait(false);
        return port;
    }

    private static SubgraphDefinition CollectSubgraph(Node root) {
        var nodes = new List<SubgraphNode>();
        var edges = new List<SubgraphEdge>();
        var visitedPaths = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<SubgraphNode>();

        queue.Enqueue(new SubgraphNode(root, new NodePath(), null));

        while (queue.Count > 0) {
            var node = queue.Dequeue();
            if (!visitedPaths.Add(node.Path.ToString()))
                continue;

            nodes.Add(node);

            foreach (var child in node.Node.AttachedNodes)
                queue.Enqueue(new SubgraphNode(
                    child,
                    new NodePath(node.Path.Concat([child.LocalId])),
                    node.Path.ToString().Length == 0 ? null : node.Path));
        }

        return new SubgraphDefinition(nodes, edges);
    }

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) => new(result.Status, Error: result.Error);

    private sealed record TypedEdgeShape(InternalId TypeId, IReadOnlyCollection<NodeBacking> EndpointPorts);

    private sealed record SubgraphDefinition(
        IReadOnlyCollection<SubgraphNode> Nodes,
        IReadOnlyCollection<SubgraphEdge> Edges);

    private sealed record SubgraphNode(Node Node, NodePath Path, NodePath? ParentPath);

    private readonly record struct SubgraphEdge(NodePath SourceId, NodePath TargetId);


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
        var internalState = await GetTypeNode(node.Backing);
        if (internalState is null)
            return null;
        return new NodeType(internalState);
    }
    internal async Task<NodeBacking?> GetTypeNode(NodeBacking node) {
        if (await node.Nodes.AnyAsync(neighbor => neighbor.GlobalId == TypesRoot.GlobalId))
            return node;
        var typeNode = await node.Nodes.SelectMany(x => x.Nodes).FirstOrDefaultAsync(typeRoot => typeRoot.GlobalId == TypesRoot.GlobalId);
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
