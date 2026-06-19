using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService {
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
}
