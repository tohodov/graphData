using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string EdgePortKind = "edge-port";
    const string SourcePortRole = "source";
    const string TargetPortRole = "target";
    const string TypePortRole = "type";

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
        if (!GraphRuntimeMetadata.IsNodeType(typeResult.Value))
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

        return await storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = [nodeId, typeId],
            MaxDepth = 1
        }).ConfigureAwait(false);
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
        if (!IsEdgeType(type))
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeGlobalId}' is not an edge type.");
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

        var relationRootId = relationRootGlobalId ?? (relationGlobalId is NodePath existingRelationId ? ParentOf(existingRelationId) : throw new Exception("Relation root is required."));
        var relationRootResult = await storage.Get(relationRootId);
        if (relationRootResult.Status != ServiceResultStatus.Ok || relationRootResult.Value == null)
            return ServiceResult<Subgraph>.NotFound(relationRootId.ToString());
        var relationRoot = relationRootResult.Value;

        var localId = !string.IsNullOrWhiteSpace(relationLocalId)
            ? relationLocalId.Trim()
            : relationGlobalId is NodePath existingRelationIdForLocalId
                ? LocalIdOf(existingRelationIdForLocalId)
                : CreateRelationLocalId(type.GlobalId);

        var ensureRoot = await EnsurePathAsync(relationRoot.GlobalId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "relation-root",
            [GraphRuntimeAttributeNames.GraphElement] = GraphRuntimeMetadata.EdgeElement
        });
        if (ensureRoot.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(ensureRoot);

        return await CreateTypedEdgeRelationSubgraphAsync(
            endpointsResult.Value.SourceId,
            endpointsResult.Value.TargetId,
            type.GlobalId,
            relationRoot.GlobalId,
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

    public Task<ServiceResult<Subgraph>> GetSubgraphAsync(
        IEnumerable<NodeRef> globalIds,
        int maxDepth) {
        return storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = globalIds.ToArray(),
            MaxDepth = maxDepth
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

    private static bool IsEdgeRelation(NodeState node) =>
        GraphRuntimeMetadata.HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, GraphRuntimeMetadata.EdgeInstanceKind) ||
        GraphRuntimeMetadata.HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, GraphRuntimeMetadata.EdgeElement)
        && node.Attributes.ContainsKey(GraphRuntimeAttributeNames.GraphTypeName);

    private static bool IsEdgeType(NodeState node) => GraphRuntimeMetadata.IsEdgeType(node);

    private static bool IsSourcePort(NodeState node) => IsPort(node, SourcePortRole);

    private static bool IsTargetPort(NodeState node) => IsPort(node, TargetPortRole);

    private static bool IsPort(NodeState node, string role) =>
        GraphRuntimeMetadata.HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, EdgePortKind)
        && GraphRuntimeMetadata.HasAttribute(node, GraphRuntimeAttributeNames.GraphRole, role);

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
        if (!IsEdgeRelation(relation))
            return ServiceResult<EdgeEndpoints>.BadRequest($"Node '{relationId}' is not a typed edge relation.");

        var connectedResult = await storage.GetConnectedNodesAsync(relation);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<EdgeEndpoints>.From(connectedResult);

        var sourcePort = connectedResult.Value.FirstOrDefault(IsSourcePort);
        var targetPort = connectedResult.Value.FirstOrDefault(IsTargetPort);
        if (sourcePort is null || targetPort is null)
            return ServiceResult<EdgeEndpoints>.BadRequest($"Relation '{relationId}' does not have source/target ports.");

        var sourceResult = await ResolvePortEndpointAsync(sourcePort, relation.LocalId, SourcePortRole);
        if (sourceResult.Status != ServiceResultStatus.Ok || sourceResult.Value is null)
            return ServiceResult<EdgeEndpoints>.From(sourceResult);
        var targetResult = await ResolvePortEndpointAsync(targetPort, relation.LocalId, TargetPortRole);
        if (targetResult.Status != ServiceResultStatus.Ok || targetResult.Value is null)
            return ServiceResult<EdgeEndpoints>.From(targetResult);

        return ServiceResult<EdgeEndpoints>.Ok(new EdgeEndpoints(sourceResult.Value, targetResult.Value));
    }

    private async Task<ServiceResult<InternalId>> ResolvePortEndpointAsync(
        NodeState port,
        NodeLocalId relationId,
        string role) {
        var connectedResult = await storage.GetConnectedNodesAsync(port);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<InternalId>.From(connectedResult);

        var endpoint = connectedResult.Value.FirstOrDefault(node => node.LocalId != relationId);
        return endpoint is null
            ? ServiceResult<InternalId>.BadRequest($"Relation port '{role}' does not have an endpoint.")
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

        var resolvedRelationRootId = relationRootId ?? GraphSystemNodeIds.RelationRoot;
        var resolvedRelationRootResult = await storage.Get(resolvedRelationRootId);
        if (resolvedRelationRootResult.Status != ServiceResultStatus.Ok || resolvedRelationRootResult.Value == null)
            return ServiceResult<Subgraph>.NotFound(resolvedRelationRootId.ToString());
        var resolvedRelationRoot = resolvedRelationRootResult.Value;

        var localId = !string.IsNullOrWhiteSpace(relationLocalId)
            ? relationLocalId.Trim()
            : CreateRelationLocalId(typeId);

        var shitAttributes = new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "relation-root",
            [GraphRuntimeAttributeNames.GraphElement] = GraphRuntimeMetadata.EdgeElement
        };
        var ensureRoot = await EnsurePathAsync(resolvedRelationRoot.GlobalId, shitAttributes).ConfigureAwait(false);
        if (ensureRoot.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(ensureRoot);

        return await CreateRegisteredEdgeRelationSubgraphAsync(
            endpointIds.ToArray(),
            typeId,
            definition,
            resolvedRelationRoot.GlobalId,
            localId).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Subgraph>> CreateRegisteredEdgeRelationSubgraphAsync(
        IReadOnlyList<NodeRef> endpointIds,
        InternalId typeId,
        EdgeTypeDefinition definition,
        InternalId relationRootId,
        string relationLocalId)
    {
        var relationResult = await storage.Create(new NodeLocalId(relationLocalId), relationRootId).ConfigureAwait(false);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<Subgraph>.From(relationResult);

        var relation = relationResult.Value;
        var connectType = await storage.Connect(relation.GlobalId, typeId).ConfigureAwait(false);
        if (connectType.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connectType);

        var endpoints = definition.Endpoints.ToArray();
        for (var index = 0; index < endpoints.Length; index++) {
            var endpoint = endpoints[index];
            var endpointInstanceResult = await storage.Create(new NodeLocalId(CreateEndpointInstanceLocalId(endpoint.Name)), relation.GlobalId).ConfigureAwait(false);
            if (endpointInstanceResult.Status != ServiceResultStatus.Ok || endpointInstanceResult.Value is null)
                return ServiceResult<Subgraph>.From(endpointInstanceResult);

            var endpointInstanceId = endpointInstanceResult.Value.GlobalId;
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

        return await storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = [relation.GlobalId],
            MaxDepth = 2
        }).ConfigureAwait(false);
    }

    private async Task<ServiceResult<Subgraph>> CreateTypedEdgeRelationSubgraphAsync(
        InternalId sourceId,
        InternalId targetId,
        InternalId typeId,
        InternalId relationRootId,
        string relationLocalId) {
        var relationResult = await storage.Create(new NodeLocalId(relationLocalId), relationRootId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = GraphRuntimeMetadata.EdgeInstanceKind,
            [GraphRuntimeAttributeNames.GraphElement] = GraphRuntimeMetadata.EdgeElement,
            [GraphRuntimeAttributeNames.GraphTypeName] = typeId.ToString()
        });
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult<Subgraph>.From(relationResult);

        var relation = relationResult.Value;
        var sourcePort = await CreatePortAsync(relation.GlobalId, SourcePortRole);
        if (sourcePort.Status != ServiceResultStatus.Ok || sourcePort.Value is null)
            return ServiceResult<Subgraph>.From(sourcePort);
        var targetPort = await CreatePortAsync(relation.GlobalId, TargetPortRole);
        if (targetPort.Status != ServiceResultStatus.Ok || targetPort.Value is null)
            return ServiceResult<Subgraph>.From(targetPort);
        var typePort = await CreatePortAsync(relation.GlobalId, TypePortRole);
        if (typePort.Status != ServiceResultStatus.Ok || typePort.Value is null)
            return ServiceResult<Subgraph>.From(typePort);

        var connect = await storage.Connect(sourcePort.Value.GlobalId, sourceId);
        if (connect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connect);
        connect = await storage.Connect(targetPort.Value.GlobalId, targetId);
        if (connect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connect);
        connect = await storage.Connect(typePort.Value.GlobalId, typeId);
        if (connect.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(connect);

        return await storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = [relation.GlobalId],
            MaxDepth = 2
        });
    }

    private Task<ServiceResult<NodeState>> CreatePortAsync(InternalId relationId, string role) =>
        storage.Create(new NodeLocalId(CreatePortLocalId(role)), relationId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = EdgePortKind,
            [GraphRuntimeAttributeNames.GraphRole] = role
        });

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

    private static InternalId ParentOf(NodePath id) {
        var segments = id.ToArray();
        return new InternalId(segments.Take(Math.Max(0, segments.Length - 1)));
    }

    private static string LocalIdOf(NodePath id) => id.ToArray().LastOrDefault().ToString() ?? CreateRelationLocalId(id);

    private static string CreateRelationLocalId(NodeRef typeId) {//TODO revisit relation id generation
        return $"{"edge"}-{Guid.NewGuid():N}"[..^24];
    }

    private static string CreatePortLocalId(string role) =>
        $"port-{role}-{Guid.NewGuid():N}"[..^24];

    private static string CreateEndpointInstanceLocalId(string endpointName) =>
        $"endpoint-{endpointName}";

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) =>
        new(result.Status, Error: result.Error);

    private sealed record EdgeEndpoints(InternalId SourceId, InternalId TargetId);
}
