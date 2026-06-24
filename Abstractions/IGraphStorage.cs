namespace Abstractions;

internal interface IGraphStorage {

    InternalId DeserializeGlobalId(string value) {
        var decoded = Uri.UnescapeDataString(value);
        var segments = decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment)
            .Select(x => new NodeLocalId(x));
        return new InternalId(segments);
    }

    InternalId Root => new InternalId(); //TODO узел

    Task<ServiceResult<NodeState>> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null);
    Task<ServiceResult<NodeState>> Get(NodeRef path);
    async Task<ServiceResult<NodeState>> GetNeighbor(NodeRef path, NodeLocalId localId) {
        var result = await Get(path);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult<NodeState>.From(result);

        var node = result.Value;
        var matches = node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId && neighbor.LocalId == localId)
            .DistinctBy(static neighbor => neighbor.GlobalId)
            .Take(2)
            .ToArray();

        return matches.Length switch {
            0 => ServiceResult<NodeState>.NotFound(),
            1 => ServiceResult<NodeState>.Ok(matches[0]),
            _ => ServiceResult<NodeState>.Conflict($"More than one neighbor with LocalId '{localId}' was found for node '{path}'.")
        };
    }
    async Task<ServiceResult<IAsyncEnumerable<NodeState>>> GetNeighbors(NodeRef path) {
        var result = await Get(path);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult<IAsyncEnumerable<NodeState>>.NotFound();
        var node = result.Value;
        var enumerable = node.Nodes;
        var asyncEnumerable = enumerable.ToAsyncEnumerable();//TODO IAsyncEnumerable
        return ServiceResult<IAsyncEnumerable<NodeState>>.Ok(asyncEnumerable);
    }
    async Task<ServiceResult> Update(NodeRef path, IDictionary<string, string> attributes) {
        var result = await Get(path);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult.From(result);

        try {
            result.Value.Attributes = attributes.ToDictionary();
        } catch (Exception ex) {
            return ServiceResult.InternalServerError(ex.ToString());
        }

        return ServiceResult.Ok();
    }
    Task<ServiceResult> Delete(NodeRef path);
    Task<ServiceResult> Connect(NodeRef sourcePath, NodeRef targetPath);
    Task<ServiceResult> Disconnect(NodeRef sourcePath, NodeRef targetPath);
    Task<ServiceResult<IReadOnlyCollection<NodeState>>> GetConnectedNodesAsync(NodeState node);
    IAsyncEnumerable<NodeState> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other);
}
