using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphService
{
    private readonly IGraphStorage _storage;
    private readonly GraphSearchService _searchService;

    internal GraphService(IGraphStorage storage, GraphSearchService searchService) {
        _storage = storage;
        _searchService = searchService;
    }

    public async Task<ServiceResult<Node>> CreateNodeAsync(
        string localId,
        IReadOnlyCollection<string>? parentGlobalId = null,
        IDictionary<string, string>? attributes = null) {
        var result = await _storage.Create(
            new NodeLocalId(localId),
            parentGlobalId is null ? null : new NodeGlobalId(parentGlobalId),
            attributes);
        return ToNodeResult(result);
    }

    public async Task<ServiceResult<Node>> GetNodeAsync(IReadOnlyCollection<string> globalId) {
        var result = await _storage.Get(new NodeGlobalId(globalId));
        return ToNodeResult(result);
    }

    public async Task<ServiceResult<Node>> GetNeighborNodeAsync(string globalId, string localId) {
        var result = await _storage.GetNeighbor(_storage.DeserializeGlobalId(globalId), new NodeLocalId(localId));
        return ToNodeResult(result);
    }

    public Task<ServiceResult> UpdateNodeAsync(IReadOnlyCollection<string> globalId, IDictionary<string, string> attributes) {
        return _storage.Update(new NodeGlobalId(globalId), attributes);
    }

    public Task<ServiceResult> DeleteNodeAsync(IReadOnlyCollection<string> globalId) {
        return _storage.Delete(new NodeGlobalId(globalId));
    }

    public Task<ServiceResult> ConnectNodesAsync(
        IReadOnlyCollection<string> sourceGlobalId,
        IReadOnlyCollection<string> targetGlobalId) {
        return _storage.Connect(new NodeGlobalId(sourceGlobalId), new NodeGlobalId(targetGlobalId));
    }

    public Task<ServiceResult<Subgraph>> GetSubgraphAsync(
        IEnumerable<IReadOnlyCollection<string>> globalIds,
        int maxDepth) {
        return _storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = globalIds.Select(static globalId => new NodeGlobalId(globalId)).ToArray(),
            MaxDepth = maxDepth
        });
    }

    public IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(
        NodeSearchQuery query,
        CancellationToken cancellationToken = default) {
        return _searchService.SearchNodesStreamAsync(query, cancellationToken);
    }

    private static ServiceResult<Node> ToNodeResult(ServiceResult<NodeState> result) {
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<Node>.Ok(new Node(result.Value))
            : ServiceResult<Node>.From(result);
    }
}
