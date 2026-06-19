using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string EdgeElement = "edge";
    const string EdgeInstanceKind = "edge-instance";
    const string EdgePortKind = "edge-port";
    const string TypeKind = "type";
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
        NodeGlobalId? typeId,
        IDictionary<string, string>? attributes) {
        var result = await storage.Create(localId, path, attributes);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToNodeResult(result);

        if (typeId is null)
            return ToNodeResult(result);

        var assign = await AssignNodeTypeAsync(result.Value.GlobalId, typeId.Value).ConfigureAwait(false);
        if (assign.Status != ServiceResultStatus.Ok) {
            await storage.Delete(result.Value.GlobalId).ConfigureAwait(false);
            return ServiceResult<Node>.From(assign);
        }

        var reloaded = await storage.Get(result.Value.GlobalId).ConfigureAwait(false);
        return ToNodeResult(reloaded);
    }

    public async Task<ServiceResult<Node>> GetNodeAsync(IReadOnlyCollection<string> globalId) {
        var result = await storage.Get(new NodeGlobalId(globalId));
        return ToNodeResult(result);
    }

    public async Task<ServiceResult<Node>> GetNeighborNodeAsync(string globalId, string localId) {
        var result = await storage.GetNeighbor(storage.DeserializeGlobalId(globalId), new NodeLocalId(localId));
        return ToNodeResult(result);
    }

    public Task<ServiceResult> UpdateNodeAsync(IReadOnlyCollection<string> globalId, IDictionary<string, string> attributes) {
        return storage.Update(new NodeGlobalId(globalId), attributes);
    }

    public Task<ServiceResult> DeleteNodeAsync(IReadOnlyCollection<string> globalId) {
        return storage.Delete(new NodeGlobalId(globalId));
    }

    public Task<ServiceResult> ConnectNodesAsync(
        IReadOnlyCollection<string> sourceGlobalId,
        IReadOnlyCollection<string> targetGlobalId) {
        return storage.Connect(new NodeGlobalId(sourceGlobalId), new NodeGlobalId(targetGlobalId));
    }

    public async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(
        IReadOnlyCollection<string> nodeGlobalId,
        IReadOnlyCollection<string> typeGlobalId) {
        var nodeId = new NodeGlobalId(nodeGlobalId);
        var typeId = new NodeGlobalId(typeGlobalId);
        return await AssignNodeTypeAsync(nodeId, typeId).ConfigureAwait(false);
    }

    public Task<ServiceResult<Subgraph>> AssignNodeTypeAsync<TNodeType>(
        IReadOnlyCollection<string> nodeGlobalId)
        where TNodeType : NodeType
    {
        return AssignNodeTypeAsync(
            new NodeGlobalId(nodeGlobalId),
            runtimeTypes.GetNodeTypeId(typeof(TNodeType)));
    }

    private async Task<ServiceResult<Subgraph>> AssignNodeTypeAsync(
        NodeGlobalId nodeId,
        NodeGlobalId typeId) {
        if (!NodeType.IsNodeTypeId(typeId))
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not under node type root '{GraphSystemNodeIds.NodeTypeRoot}'.");

        var typeResult = await storage.Get(typeId).ConfigureAwait(false);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);
        var typeNode = NodeType.FromState(typeResult.Value);

        var nodeResult = await storage.Get(nodeId).ConfigureAwait(false);
        if (nodeResult.Status != ServiceResultStatus.Ok || nodeResult.Value is null)
            return ServiceResult<Subgraph>.From(nodeResult);

        var alreadyAssigned = await IsConnectedAsync(nodeResult.Value, typeId).ConfigureAwait(false);
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
            var definition = runtimeTypes.TryCreateNodeTypeDefinition(typeId, typeNode, out var registeredDefinition)
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
        IReadOnlyCollection<string>? sourceGlobalId,
        IReadOnlyCollection<string>? targetGlobalId,
        IReadOnlyCollection<string> typeGlobalId,
        IReadOnlyCollection<string>? relationGlobalId = null,
        IReadOnlyCollection<string>? relationRootGlobalId = null,
        string? relationLocalId = null) {
        var sourceId = ToOptionalGlobalId(sourceGlobalId);
        var targetId = ToOptionalGlobalId(targetGlobalId);
        var typeId = new NodeGlobalId(typeGlobalId);
        var oldRelationId = ToOptionalGlobalId(relationGlobalId);

        var endpointsResult = await ResolveEdgeEndpointsAsync(sourceId, targetId, oldRelationId);
        if (endpointsResult.Status != ServiceResultStatus.Ok || endpointsResult.Value is null)
            return ServiceResult<Subgraph>.From(endpointsResult);

        var typeResult = await storage.Get(typeId);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult<Subgraph>.From(typeResult);

        var type = typeResult.Value;
        if (!IsEdgeType(type))
            return ServiceResult<Subgraph>.BadRequest($"Node '{typeId}' is not an edge type.");

        if (oldRelationId is null) {
            var disconnect = await DisconnectBasicEdgeIfPresentAsync(endpointsResult.Value.SourceId, endpointsResult.Value.TargetId);
            if (disconnect.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(disconnect);
        } else {
            var delete = await storage.Delete(oldRelationId.Value);
            if (delete.Status != ServiceResultStatus.Ok)
                return ToSubgraphResult(delete);
        }

        var relationRootId = ToOptionalGlobalId(relationRootGlobalId)
            ?? (oldRelationId is { } existingRelationId ? ParentOf(existingRelationId) : GraphSystemNodeIds.RelationRoot);
        var localId = !string.IsNullOrWhiteSpace(relationLocalId)
            ? relationLocalId.Trim()
            : oldRelationId is { } existingRelationIdForLocalId
                ? LocalIdOf(existingRelationIdForLocalId)
                : CreateRelationLocalId(typeId);

        var ensureRoot = await EnsurePathAsync(relationRootId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "relation-root",
            [GraphRuntimeAttributeNames.GraphElement] = EdgeElement
        });
        if (ensureRoot.Status != ServiceResultStatus.Ok)
            return ToSubgraphResult(ensureRoot);

        return await CreateTypedEdgeRelationSubgraphAsync(
            endpointsResult.Value.SourceId,
            endpointsResult.Value.TargetId,
            typeId,
            relationRootId,
            localId);
    }

    public Task<ServiceResult<Subgraph>> GetSubgraphAsync(
        IEnumerable<IReadOnlyCollection<string>> globalIds,
        int maxDepth) {
        return storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = globalIds.Select(static globalId => new NodeGlobalId(globalId)).ToArray(),
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

    private static ServiceResult<Node> ToNodeResult(ServiceResult<NodeState> result) {
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<Node>.Ok(new Node(result.Value))
            : ServiceResult<Node>.From(result);
    }

    private static bool IsEdgeRelation(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, EdgeInstanceKind) ||
        HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement)
        && node.Attributes.ContainsKey(GraphRuntimeAttributeNames.GraphTypeName);

    private static bool IsEdgeType(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, TypeKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement)
        || IsChildOf(node.GlobalId, GraphSystemNodeIds.EdgeTypeRoot);

    private static bool IsSourcePort(NodeState node) => IsPort(node, SourcePortRole);

    private static bool IsTargetPort(NodeState node) => IsPort(node, TargetPortRole);

    private static bool IsPort(NodeState node, string role) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, EdgePortKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphRole, role);

    private static bool HasAttribute(NodeState node, string key, string value) =>
        node.Attributes.TryGetValue(key, out var actual)
        && string.Equals(actual, value, StringComparison.Ordinal);

    private static bool IsChildOf(NodeGlobalId id, NodeGlobalId root) {
        var idSegments = id.ToArray();
        var rootSegments = root.ToArray();
        if (idSegments.Length <= rootSegments.Length)
            return false;

        for (var index = 0; index < rootSegments.Length; index++)
            if (idSegments[index] != rootSegments[index])
                return false;

        return true;
    }

    private async Task<ServiceResult<EdgeEndpoints>> ResolveEdgeEndpointsAsync(
        NodeGlobalId? sourceId,
        NodeGlobalId? targetId,
        NodeGlobalId? relationId) {
        if (sourceId is { } source && targetId is { } target) {
            if (source == target)
                return ServiceResult<EdgeEndpoints>.BadRequest("Source and target nodes must be different.");

            var sourceResult = await storage.Get(source);
            if (sourceResult.Status != ServiceResultStatus.Ok || sourceResult.Value is null)
                return ServiceResult<EdgeEndpoints>.From(sourceResult);
            var targetResult = await storage.Get(target);
            if (targetResult.Status != ServiceResultStatus.Ok || targetResult.Value is null)
                return ServiceResult<EdgeEndpoints>.From(targetResult);

            return ServiceResult<EdgeEndpoints>.Ok(new EdgeEndpoints(source, target));
        }

        if (sourceId is not null || targetId is not null)
            return ServiceResult<EdgeEndpoints>.BadRequest("Both sourceGlobalId and targetGlobalId must be provided.");

        if (relationId is null)
            return ServiceResult<EdgeEndpoints>.BadRequest("Provide either source/target nodes or relationGlobalId.");

        return await ResolveRelationEndpointsAsync(relationId.Value);
    }

    private async Task<ServiceResult<EdgeEndpoints>> ResolveRelationEndpointsAsync(NodeGlobalId relationId) {
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

        var sourceResult = await ResolvePortEndpointAsync(sourcePort, relationId, SourcePortRole);
        if (sourceResult.Status != ServiceResultStatus.Ok)
            return ServiceResult<EdgeEndpoints>.From(sourceResult);
        var targetResult = await ResolvePortEndpointAsync(targetPort, relationId, TargetPortRole);
        if (targetResult.Status != ServiceResultStatus.Ok)
            return ServiceResult<EdgeEndpoints>.From(targetResult);

        return ServiceResult<EdgeEndpoints>.Ok(new EdgeEndpoints(sourceResult.Value, targetResult.Value));
    }

    private async Task<ServiceResult<NodeGlobalId>> ResolvePortEndpointAsync(
        NodeState port,
        NodeGlobalId relationId,
        string role) {
        var connectedResult = await storage.GetConnectedNodesAsync(port);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<NodeGlobalId>.From(connectedResult);

        var endpoint = connectedResult.Value.FirstOrDefault(node => node.GlobalId != relationId);
        return endpoint is null
            ? ServiceResult<NodeGlobalId>.BadRequest($"Relation port '{role}' does not have an endpoint.")
            : ServiceResult<NodeGlobalId>.Ok(endpoint.GlobalId);
    }

    private async Task<ServiceResult> DisconnectBasicEdgeIfPresentAsync(NodeGlobalId sourceId, NodeGlobalId targetId) {
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

    private async Task<ServiceResult<bool>> IsConnectedAsync(NodeState node, NodeGlobalId targetId) {
        var connectedResult = await storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<bool>.From(connectedResult);

        return ServiceResult<bool>.Ok(connectedResult.Value.Any(neighbor => neighbor.GlobalId == targetId));
    }

    private async Task<ServiceResult<Subgraph>> CreateTypedEdgeRelationSubgraphAsync(
        NodeGlobalId sourceId,
        NodeGlobalId targetId,
        NodeGlobalId typeId,
        NodeGlobalId relationRootId,
        string relationLocalId) {
        var relationResult = await storage.Create(new NodeLocalId(relationLocalId), relationRootId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = EdgeInstanceKind,
            [GraphRuntimeAttributeNames.GraphElement] = EdgeElement,
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

    private Task<ServiceResult<NodeState>> CreatePortAsync(NodeGlobalId relationId, string role) =>
        storage.Create(new NodeLocalId(CreatePortLocalId(role)), relationId, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = EdgePortKind,
            [GraphRuntimeAttributeNames.GraphRole] = role
        });

    private async Task<ServiceResult> EnsurePathAsync(NodeGlobalId id, IDictionary<string, string>? leafAttributes = null) {
        var segments = id.ToArray();
        for (var index = 0; index < segments.Length; index++) {
            var current = new NodeGlobalId(segments.Take(index + 1));
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

    private static NodeGlobalId? ToOptionalGlobalId(IReadOnlyCollection<string>? segments) =>
        segments is null || segments.Count == 0 ? null : new NodeGlobalId(segments);

    private static NodeGlobalId ParentOf(NodeGlobalId id) {
        var segments = id.ToArray();
        return new NodeGlobalId(segments.Take(Math.Max(0, segments.Length - 1)));
    }

    private static string LocalIdOf(NodeGlobalId id) =>
        id.ToArray().LastOrDefault().ToString() ?? CreateRelationLocalId(id);

    private static string CreateRelationLocalId(NodeGlobalId typeId) {
        var typeName = LocalIdOfType(typeId);
        var safeTypeName = new string(typeName
            .Select(static character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeTypeName))
            safeTypeName = "edge";

        return $"{safeTypeName}-{Guid.NewGuid():N}"[..^24];
    }

    private static string CreatePortLocalId(string role) =>
        $"port-{role}-{Guid.NewGuid():N}"[..^24];

    private static string LocalIdOfType(NodeGlobalId id) {
        var segments = id.ToArray();
        return segments.Length == 0 ? "edge" : segments[^1].ToString();
    }

    private static ServiceResult<Subgraph> ToSubgraphResult(ServiceResult result) =>
        new(result.Status, Error: result.Error);

    private sealed record EdgeEndpoints(NodeGlobalId SourceId, NodeGlobalId TargetId);
}
