using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";

    readonly IGraphStorage storage;
    readonly GraphSearchService searchService;
    readonly ICancellationTokenAccessor cancellationTokens;
    readonly GraphRuntimeTypeCatalog runtimeTypes;

    internal GraphService(IGraphStorage storage, GraphSearchService searchService, ICancellationTokenAccessor cancellationTokens)
        : this(storage, searchService, cancellationTokens, GraphRuntimeTypeCatalog.Create()) {
    }

    internal GraphService(
        IGraphStorage storage,
        GraphSearchService searchService,
        ICancellationTokenAccessor cancellationTokens,
        params Assembly[] runtimeTypeAssemblies)
        : this(storage, searchService, cancellationTokens, GraphRuntimeTypeCatalog.Create(runtimeTypeAssemblies)) {
    }

    internal GraphService(
        IGraphStorage storage,
        GraphSearchService searchService,
        ICancellationTokenAccessor cancellationTokens,
        GraphRuntimeTypeCatalog runtimeTypes) {
        this.storage = storage;
        this.searchService = searchService;
        this.cancellationTokens = cancellationTokens;
        this.runtimeTypes = runtimeTypes;
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodePath? path = null, NodeType? type = null, IDictionary<string, string>? attributes = null) {
        return await CreateNode(localId, path, type?.GlobalId, attributes).ConfigureAwait(false);
    }

    public Task<ServiceResult<Node>> CreateNode<TNodeType>(
        NodeLocalId localId,
        NodePath? path = null,
        IDictionary<string, string>? attributes = null)
        where TNodeType : NodeType
    {
        var typeId = runtimeTypes.GetNodeTypeId(typeof(TNodeType));
        return CreateNode(localId, path, typeId, attributes);
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

    public Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(NodePath nodeId)
        where TNodeType : NodeType
    {
        return AssignNodeTypeAsync(
            new InternalId(nodeId),
            runtimeTypes.GetNodeTypeId(typeof(TNodeType)));
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(
        NodeRef nodeId,
        NodeRef typeId) {

        var typeResult = await storage.Get(typeId).ConfigureAwait(false);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);
        var typeNode = NodeType.FromState(typeResult.Value);
        if (!GraphTypeTopology.IsNodeType(typeResult.Value))
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

        try {
            var definition = runtimeTypes.TryCreateNodeTypeDefinition(typeNode.GlobalId, typeNode, out var registeredDefinition)
                ? registeredDefinition
                : typeNode.Define();
            definition.EnsureSatisfiedBy(new InstanceNode(reloadedNode.Value));
        } catch (InvalidOperationException ex) {
            if (!alreadyAssigned.Value)
                await storage.Disconnect(nodeId, typeId).ConfigureAwait(false);
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        return await GetSubgraph([nodeId, typeId], 1).ConfigureAwait(false);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync(
        NodeRef? sourceGlobalId,
        NodeRef? targetGlobalId,
        NodeRef typeGlobalId,
        NodeRef? relationGlobalId = null,
        NodeRef? relationRootGlobalId = null,
        string? relationLocalId = null
    ) {
        var endpointsResult = await ResolveEdgeEndpointsAsync(sourceGlobalId, targetGlobalId, relationGlobalId);
        if (endpointsResult.Status != ServiceResultStatus.Ok || endpointsResult.Value is null)
            return ServiceResult<Subgraph>.From(endpointsResult);

        var typeResult = await storage.Get(typeGlobalId);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);

        var type = typeResult.Value;
        if (!IsRelationType(type))
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeGlobalId}' is not a relation node type.");
        if (relationGlobalId is null
            && runtimeTypes.TryCreateEdgeTypeDefinition(type.GlobalId, out var edgeTypeDefinition)) {
            return await ChangeRegisteredEdgeTypeAsync(
                [endpointsResult.Value.SourceId, endpointsResult.Value.TargetId],
                type.GlobalId,
                edgeTypeDefinition,
                relationRootGlobalId,
                relationLocalId).ConfigureAwait(false);
        }

        if (relationGlobalId is null) {
            var disconnect = await DisconnectBasicEdgeIfPresentAsync(endpointsResult.Value.SourceId, endpointsResult.Value.TargetId);
            if (disconnect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(disconnect);
        } else {
            var delete = await storage.Delete(relationGlobalId);
            if (delete.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(delete);
        }

        var relationParentId = NormalizeParentId(relationRootGlobalId)
            ?? (relationGlobalId is NodePath existingRelationId ? ParentOf(existingRelationId) : null);

        var localId = !string.IsNullOrWhiteSpace(relationLocalId)
            ? relationLocalId.Trim()
            : relationGlobalId is NodePath existingRelationIdForLocalId
                ? LocalIdOf(existingRelationIdForLocalId)
                : CreateRelationLocalId(type.GlobalId);

        if (relationParentId is not null) {
            var ensureRoot = await EnsurePathAsync(relationParentId).ConfigureAwait(false);
            if (ensureRoot.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(ensureRoot);
        }

        return await CreateTypedEdgeRelationSubgraphAsync(
            endpointsResult.Value.SourceId,
            endpointsResult.Value.TargetId,
            type.GlobalId,
            relationParentId,
            localId);
    }

    public Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TEdgeType>(
        NodeRef sourceGlobalId,
        NodeRef targetGlobalId,
        NodeRef? relationRootGlobalId = null,
        string? relationLocalId = null)
        where TEdgeType : EdgeType
    {
        return ChangeEdgeTypeAsync<TEdgeType>(
            [sourceGlobalId, targetGlobalId],
            relationRootGlobalId,
            relationLocalId);
    }

    public async Task<ServiceResult<Subgraph>> ChangeEdgeTypeAsync<TEdgeType>(
        IReadOnlyCollection<NodeRef> endpointGlobalIds,
        NodeRef? relationRootGlobalId = null,
        string? relationLocalId = null)
        where TEdgeType : EdgeType
    {
        var typeId = runtimeTypes.GetEdgeTypeId(typeof(TEdgeType));
        if (!runtimeTypes.TryCreateEdgeTypeDefinition(typeId, out var definition))
            return ServiceResult<Subgraph>.BadRequest($"CLR type '{typeof(TEdgeType).FullName}' is not a registered edge type.");

        return await ChangeRegisteredEdgeTypeAsync(
            endpointGlobalIds,
            typeId,
            definition,
            relationRootGlobalId,
            relationLocalId).ConfigureAwait(false);
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

            if (IsDirectHierarchyEdge(edge.SourceId, edge.TargetId))
                continue;

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
        where TNodeType : NodeType
    {
        var typeId = runtimeTypes.GetNodeTypeId(typeof(TNodeType));
        var typeResult = await storage.Get(typeId).ConfigureAwait(false);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<NodeTypeDefinition>.From(typeResult);

        var typeNode = NodeType.FromState(typeResult.Value);
        return runtimeTypes.TryCreateNodeTypeDefinition(typeId, typeNode, out var definition)
            ? ServiceResult<NodeTypeDefinition>.Ok(definition)
            : ServiceResult<NodeTypeDefinition>.Ok(typeNode.Define());
    }

    public async Task<ServiceResult<EdgeTypeDefinition>> GetEdgeTypeDefinitionAsync<TEdgeType>()
        where TEdgeType : EdgeType
    {
        var typeId = runtimeTypes.GetEdgeTypeId(typeof(TEdgeType));
        var typeResult = await storage.Get(typeId).ConfigureAwait(false);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<EdgeTypeDefinition>.From(typeResult);

        return runtimeTypes.TryCreateEdgeTypeDefinition(typeId, out var definition)
            ? ServiceResult<EdgeTypeDefinition>.Ok(definition)
            : ServiceResult<EdgeTypeDefinition>.BadRequest($"CLR type '{typeof(TEdgeType).FullName}' is not a registered edge type.");
    }

    private static ServiceResult<Node> ToNodeResult(ServiceResult<NodeState> result) {
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<Node>.Ok(new Node(result.Value))
            : ServiceResult<Node>.From(result);
    }

    private bool IsRelationType(NodeState node) =>
        GraphTypeTopology.IsNodeType(node)
        && (node.GlobalId == GraphBaseTypeIds.Relation
            || GraphTypeTopology.IsConnectedTo(node, GraphBaseTypeIds.Relation)
            || runtimeTypes.TryCreateEdgeTypeDefinition(node.GlobalId, out _));

    private async Task<ServiceResult<EdgeEndpoints>> ResolveEdgeEndpointsAsync(
        NodeRef? sourceId,
        NodeRef? targetId,
        NodeRef? relationId) {
        if (sourceId is { } source && targetId is { } target) {
            if (source == target)
                return ServiceResult<EdgeEndpoints>.BadRequest("Source and target nodes must be different.");

            var sourceResult = await storage.Get(source);
            if (sourceResult.Status != ServiceResultStatus.Ok || sourceResult.Value is null)
                return ServiceResult<EdgeEndpoints>.From(sourceResult);
            var targetResult = await storage.Get(target);
            if (targetResult.Status != ServiceResultStatus.Ok || targetResult.Value is null)
                return ServiceResult<EdgeEndpoints>.From(targetResult);

            return ServiceResult<EdgeEndpoints>.Ok(new EdgeEndpoints(sourceResult.Value.GlobalId, targetResult.Value.GlobalId));
        }

        if (sourceId is not null || targetId is not null)
            return ServiceResult<EdgeEndpoints>.BadRequest("Both sourceGlobalId and targetGlobalId must be provided.");

        if (relationId is null)
            return ServiceResult<EdgeEndpoints>.BadRequest("Provide either source/target nodes or relationGlobalId.");

        return await ResolveRelationEndpointsAsync(relationId);
    }

    private async Task<ServiceResult<EdgeEndpoints>> ResolveRelationEndpointsAsync(NodeRef relationId) {
        var relationResult = await storage.Get(relationId);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<EdgeEndpoints>.From(relationResult);

        var relation = relationResult.Value;
        var connectedResult = await storage.GetConnectedNodesAsync(relation);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<EdgeEndpoints>.From(connectedResult);

        var relationShape = await ResolveRelationShapeAsync(relation, connectedResult.Value).ConfigureAwait(false);
        if (relationShape.Status != ServiceResultStatus.Ok || relationShape.Value is null)
            return ServiceResult<EdgeEndpoints>.From(relationShape);

        var endpoints = new List<InternalId>();
        foreach (var endpointPort in relationShape.Value.EndpointPorts) {
            var endpoint = await ResolvePortEndpointAsync(
                endpointPort,
                relation.GlobalId,
                relationShape.Value.EdgeTypeId).ConfigureAwait(false);
            if (endpoint.Status != ServiceResultStatus.Ok || endpoint.Value is null)
                return ServiceResult<EdgeEndpoints>.From(endpoint);
            endpoints.Add(endpoint.Value);
        }

        if (endpoints.Count < 2)
            return ServiceResult<EdgeEndpoints>.BadRequest($"Relation '{relationId}' does not have source/target ports.");

        return ServiceResult<EdgeEndpoints>.Ok(new EdgeEndpoints(endpoints[0], endpoints[1]));
    }

    private async Task<ServiceResult<RelationShape>> ResolveRelationShapeAsync(
        NodeState relation,
        IReadOnlyCollection<NodeState> connected)
    {
        var directRelationType = connected.FirstOrDefault(IsRelationType);
        if (directRelationType is not null) {
            var endpointPorts = connected
                .Where(node => node.GlobalId != directRelationType.GlobalId)
                .Where(static node => !GraphTypeTopology.IsGraphType(node))
                .ToArray();
            return ServiceResult<RelationShape>.Ok(new RelationShape(directRelationType.GlobalId, endpointPorts));
        }

        foreach (var candidatePort in connected) {
            var candidateConnections = await storage.GetConnectedNodesAsync(candidatePort).ConfigureAwait(false);
            if (candidateConnections.Status != ServiceResultStatus.Ok || candidateConnections.Value is null)
                return ServiceResult<RelationShape>.From(candidateConnections);

            var relationType = candidateConnections.Value.FirstOrDefault(IsRelationType);
            if (relationType is null)
                continue;

            var endpointPorts = connected
                .Where(node => node.GlobalId != candidatePort.GlobalId)
                .Where(static node => !GraphTypeTopology.IsGraphType(node))
                .ToArray();
            return ServiceResult<RelationShape>.Ok(new RelationShape(relationType.GlobalId, endpointPorts));
        }

        return ServiceResult<RelationShape>.BadRequest($"Node '{relation.GlobalId}' is not a typed edge relation.");
    }

    private async Task<ServiceResult<InternalId>> ResolvePortEndpointAsync(
        NodeState port,
        InternalId relationId,
        InternalId edgeTypeId) {
        var connectedResult = await storage.GetConnectedNodesAsync(port);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<InternalId>.From(connectedResult);

        var endpoint = connectedResult.Value
            .Where(node => node.GlobalId != relationId)
            .Where(node => node.GlobalId != edgeTypeId)
            .Where(static node => !GraphTypeTopology.IsGraphType(node))
            .Where(node => !IsChildOf(node.GlobalId, edgeTypeId))
            .FirstOrDefault(node => !GraphTypeTopology.IsConnectedTo(node, edgeTypeId));
        return endpoint is null
            ? ServiceResult<InternalId>.BadRequest($"Relation port '{port.GlobalId}' does not have an endpoint.")
            : ServiceResult<InternalId>.Ok(endpoint.GlobalId);
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

    private async Task<ServiceResult<Subgraph>> ChangeRegisteredEdgeTypeAsync(
        IReadOnlyCollection<NodeRef> endpointIds,
        InternalId typeId,
        EdgeTypeDefinition definition,
        NodeRef? relationRootId,
        string? relationLocalId)
    {
        if (endpointIds.Count < 2)
            return ServiceResult<Subgraph>.BadRequest("Typed edge requires at least two endpoint nodes.");

        var endpointStates = new List<NodeState>(endpointIds.Count);
        foreach (var endpointId in endpointIds) {
            var endpointResult = await storage.Get(endpointId).ConfigureAwait(false);
            if (endpointResult.Status != ServiceResultStatus.Ok || endpointResult.Value is null)
                return ServiceResult<Subgraph>.From(endpointResult);
            endpointStates.Add(endpointResult.Value);
        }

        try {
            definition.EnsureSatisfiedBy(endpointStates.Select(static state => new InstanceNode(state)).ToArray());
        } catch (InvalidOperationException ex) {
            return ServiceResult<Subgraph>.BadRequest(ex.Message);
        }

        if (endpointIds.Count == 2) {
            var disconnect = await DisconnectBasicEdgeIfPresentAsync(endpointIds.ElementAt(0), endpointStates.ElementAt(1).GlobalId).ConfigureAwait(false);
            if (disconnect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(disconnect);
        }

        var relationParentId = NormalizeParentId(relationRootId);

        var localId = !string.IsNullOrWhiteSpace(relationLocalId)
            ? relationLocalId.Trim()
            : CreateRelationLocalId(typeId);

        if (relationParentId is not null) {
            var ensureRoot = await EnsurePathAsync(relationParentId).ConfigureAwait(false);
            if (ensureRoot.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(ensureRoot);
        }

        return await CreateRegisteredEdgeRelationSubgraphAsync(
            endpointIds.ToArray(),
            typeId,
            definition,
            relationParentId,
            localId).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Subgraph>> CreateRegisteredEdgeRelationSubgraphAsync(
        IReadOnlyList<NodeRef> endpointIds,
        InternalId typeId,
        EdgeTypeDefinition definition,
        InternalId? relationParentId,
        string relationLocalId)
    {
        var relationResult = await storage.Create(new NodeLocalId(relationLocalId), relationParentId).ConfigureAwait(false);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<Subgraph>.From(relationResult);

        var relation = relationResult.Value;
        var connectType = await storage.Connect(relation.GlobalId, typeId).ConfigureAwait(false);
        if (connectType.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connectType);

        var ensureEndpointType = await EnsurePathAsync(GraphBaseTypeIds.Endpoint).ConfigureAwait(false);
        if (ensureEndpointType.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(ensureEndpointType);

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstanceResult = await storage.Create(new NodeLocalId(CreateEndpointInstanceLocalId(endpoint.Name)), relation.GlobalId).ConfigureAwait(false);
            if (endpointInstanceResult.Status != ServiceResultStatus.Ok || endpointInstanceResult.Value is null)
                return ServiceResult<Subgraph>.From(endpointInstanceResult);

            var endpointInstanceId = endpointInstanceResult.Value.GlobalId;
            var connectEndpointType = await storage.Connect(endpointInstanceId, GraphBaseTypeIds.Endpoint).ConfigureAwait(false);
            if (connectEndpointType.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connectEndpointType);

            var connectEndpoint = await storage.Connect(endpointInstanceId, endpointIds[index]).ConfigureAwait(false);
            if (connectEndpoint.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connectEndpoint);

            var endpointSpecId = new InternalId(typeId.Concat([new NodeLocalId(endpoint.Name)]));
            var ensureEndpointSpec = await EnsurePathAsync(endpointSpecId).ConfigureAwait(false);
            if (ensureEndpointSpec.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(ensureEndpointSpec);

            var connectSpec = await storage.Connect(endpointInstanceId, endpointSpecId).ConfigureAwait(false);
            if (connectSpec.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(connectSpec);
        }

        return await GetSubgraph([relation.GlobalId], 2).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Subgraph>> CreateTypedEdgeRelationSubgraphAsync(
        InternalId sourceId,
        InternalId targetId,
        InternalId typeId,
        InternalId? relationParentId,
        string relationLocalId) {
        var relationResult = await storage.Create(new NodeLocalId(relationLocalId), relationParentId);
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

    private async Task<ServiceResult<NodeState>> CreatePortAsync(InternalId relationId, string role)
    {
        var ensurePortType = await EnsurePathAsync(GraphBaseTypeIds.Port).ConfigureAwait(false);
        if (ensurePortType.Status != ServiceResultStatus.Ok)
            return new ServiceResult<NodeState>(ensurePortType.Status, Error: ensurePortType.Error);

        var port = await storage.Create(new NodeLocalId(CreatePortLocalId(role)), relationId).ConfigureAwait(false);
        if (port.Status != ServiceResultStatus.Ok || port.Value is null)
            return port;

        var connectType = await storage.Connect(port.Value.GlobalId, GraphBaseTypeIds.Port).ConfigureAwait(false);
        if (connectType.Status != ServiceResultStatus.Ok)
            return new ServiceResult<NodeState>(connectType.Status, Error: connectType.Error);

        return port;
    }

    private async Task<ServiceResult> EnsurePathAsync(InternalId id, IDictionary<string, string>? leafAttributes = null) {//TODO revisit path creation
        var segments = id.ToArray();
        for (var index = 0; index < segments.Length; index++) {
            var current = new InternalId(segments.Take(index + 1));
            var get = await storage.Get(current);
            if (get.Status == ServiceResultStatus.Ok)
                continue;
            if (get.Status != ServiceResultStatus.NotFound)
                return ServiceResult.From(get);

            NodePath? parent = null;
            if (index > 0)
                parent = new NodePath(segments.Take(index));
            var attributes = index == segments.Length - 1 ? leafAttributes : null;
            var create = await storage.Create(segments[index], parent, attributes);
            if (create.Status != ServiceResultStatus.Ok)
                return ServiceResult.From(create);
        }

        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<NodeState>> EnsureSubgraphNodeAsync(NodeState node) =>
        await EnsureSubgraphNodeAsync(
            node.GlobalId,
            node is VirtualNodeState virtualNode ? virtualNode.AttributeSnapshot() : null).ConfigureAwait(false);

    private async Task<ServiceResult<NodeState>> EnsureSubgraphNodeAsync(
        InternalId id,
        IDictionary<string, string>? attributes)
    {
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

    private static SubgraphDefinition CollectSubgraph(NodeState root)
    {
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

    private static InternalId? ParentOf(NodePath id) {
        var segments = id.ToArray();
        return segments.Length <= 1
            ? null
            : new InternalId(segments.Take(segments.Length - 1));
    }

    private static string LocalIdOf(NodePath id) => id.ToArray().LastOrDefault().ToString() ?? CreateRelationLocalId(id);

    private static string CreateRelationLocalId(NodeRef typeId) {//TODO revisit relation id generation
        return $"{"edge"}-{Guid.NewGuid():N}"[..^24];
    }

    private static string CreatePortLocalId(string role) =>
        $"port-{role}-{Guid.NewGuid():N}"[..^24];

    private static string CreateEndpointInstanceLocalId(string endpointName) =>
        $"endpoint-{endpointName}";

    private static InternalId? NormalizeParentId(NodeRef? id)
    {
        var segments = id switch {
            null => Array.Empty<NodeLocalId>(),
            InternalId internalId => internalId.ToArray(),
            NodePath path => path.ToArray(),
            _ => Array.Empty<NodeLocalId>()
        };

        return segments.Length == 0 ? null : new InternalId(segments);
    }

    private static bool IsChildOf(InternalId id, InternalId parentId)
    {
        var segments = id.ToArray();
        var parentSegments = parentId.ToArray();
        if (segments.Length <= parentSegments.Length)
            return false;

        return parentSegments.SequenceEqual(segments.Take(parentSegments.Length));
    }

    private static bool IsDirectHierarchyEdge(InternalId first, InternalId second) =>
        IsDirectParent(first, second) || IsDirectParent(second, first);

    private static bool IsDirectParent(InternalId parent, InternalId child)
    {
        var parentSegments = parent.ToArray();
        var childSegments = child.ToArray();
        return childSegments.Length == parentSegments.Length + 1
            && parentSegments.SequenceEqual(childSegments.Take(parentSegments.Length));
    }

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) =>
        new(result.Status, Error: result.Error);

    private sealed record EdgeEndpoints(InternalId SourceId, InternalId TargetId);

    private sealed record RelationShape(InternalId EdgeTypeId, IReadOnlyCollection<NodeState> EndpointPorts);

    private sealed record SubgraphDefinition(
        IReadOnlyDictionary<InternalId, NodeState> Nodes,
        IReadOnlyCollection<SubgraphEdge> Edges);

    private readonly record struct SubgraphEdge(InternalId SourceId, InternalId TargetId)
    {
        public static SubgraphEdge Create(InternalId first, InternalId second) =>
            string.CompareOrdinal(first.ToString(), second.ToString()) <= 0
                ? new SubgraphEdge(first, second)
                : new SubgraphEdge(second, first);
    }
}
