using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
    const string EdgeElement = "edge";
    const string EdgeInstanceKind = "edge-instance";
    const string EdgePortKind = "edge-port";
    const string TypeKind = "type";
    const string TypePortRole = "type";

    readonly IGraphStorage storage;
    readonly GraphSearchService searchService;
    readonly ICancellationTokenAccessor cancellationTokens;

    internal GraphService(IGraphStorage storage, GraphSearchService searchService, ICancellationTokenAccessor cancellationTokens) {
        this.storage = storage;
        this.searchService = searchService;
        this.cancellationTokens = cancellationTokens;
    }

    public async Task<ServiceResult<Node>> CreateNode(NodeLocalId localId, NodePath? path = null, TypeNode? type = null, IDictionary<string, string>? attributes = null) {
        var result = await storage.Create(localId, path, attributes);
        return ToNodeResult(result);
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

    public async Task<ServiceResult> ChangeEdgeTypeAsync(
        IReadOnlyCollection<string> relationGlobalId,
        IReadOnlyCollection<string> typeGlobalId) {
        var relationId = new NodeGlobalId(relationGlobalId);
        var typeId = new NodeGlobalId(typeGlobalId);

        var relationResult = await storage.Get(relationId);
        if (relationResult.Status != ServiceResultStatus.Ok || relationResult.Value is null)
            return ServiceResult.From(relationResult);

        var typeResult = await storage.Get(typeId);
        if (typeResult.Status != ServiceResultStatus.Ok || typeResult.Value is null)
            return ServiceResult.From(typeResult);

        var relation = relationResult.Value;
        var type = typeResult.Value;
        if (!IsEdgeRelation(relation))
            return ServiceResult.BadRequest($"Node '{relationId}' is not a typed edge relation.");
        if (!IsEdgeType(type))
            return ServiceResult.BadRequest($"Node '{typeId}' is not an edge type.");

        var connectorResult = await GetEdgeTypeConnectorAsync(relation);
        if (connectorResult.Status != ServiceResultStatus.Ok || connectorResult.Value is null)
            return connectorResult.Status == ServiceResultStatus.Ok
                ? ServiceResult.InternalServerError("Failed to resolve edge type connector.")
                : ServiceResult.From(connectorResult);

        var connector = connectorResult.Value;
        var connectedResult = await storage.GetConnectedNodesAsync(connector);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult.From(connectedResult);

        var alreadyConnected = false;
        foreach (var connected in connectedResult.Value.Where(IsEdgeType)) {
            if (connected.GlobalId == typeId) {
                alreadyConnected = true;
                continue;
            }

            var disconnect = await storage.Disconnect(connector.GlobalId, connected.GlobalId);
            if (disconnect.Status != ServiceResultStatus.Ok)
                return disconnect;
        }

        if (!alreadyConnected) {
            var connect = await storage.Connect(connector.GlobalId, typeId);
            if (connect.Status != ServiceResultStatus.Ok)
                return connect;
        }

        var attributes = relation.Attributes.ToDictionary();
        attributes[GraphRuntimeAttributeNames.GraphTypeName] = typeId.ToString();
        return await storage.Update(relationId, attributes);
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

    private static ServiceResult<Node> ToNodeResult(ServiceResult<NodeState> result) {
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<Node>.Ok(new Node(result.Value))
            : ServiceResult<Node>.From(result);
    }

    private async Task<ServiceResult<NodeState>> GetEdgeTypeConnectorAsync(NodeState relation) {
        var connectedResult = await storage.GetConnectedNodesAsync(relation);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<NodeState>.From(connectedResult);

        return ServiceResult<NodeState>.Ok(connectedResult.Value.FirstOrDefault(IsTypePort) ?? relation);
    }

    private static bool IsEdgeRelation(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, EdgeInstanceKind) ||
        HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement)
        && node.Attributes.ContainsKey(GraphRuntimeAttributeNames.GraphTypeName);

    private static bool IsEdgeType(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, TypeKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement)
        || IsChildOf(node.GlobalId, GraphSystemNodeIds.EdgeTypeRoot);

    private static bool IsTypePort(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, EdgePortKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphRole, TypePortRole);

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
}
