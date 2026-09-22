namespace Abstractions;

internal interface IGraphStorage {
    CarrierNodeBacking Root { get; }

    Task<CarrierNodeBacking> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null);
    Task<CarrierNodeBacking?> Get(NodeRef path);
    async Task<CarrierNodeBacking?> Get(NodeRef parent, NodeLocalId nodeId) {
        var node = await Get(parent);
        if (node is null)
            return null;
        var matches = await node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId && neighbor.LocalId == nodeId)
            .DistinctBy(static neighbor => neighbor.GlobalId)
            .Take(2)
            .ToArrayAsync();
        return matches.Length switch {
            0 => null,
            1 => matches[0],
            _ => throw new Exception("")
        };
    }
    async IAsyncEnumerable<CarrierNodeBacking> GetNeighbors(CarrierNodeBacking node) {//TODO вывернуть наоборот чтобы этот метод юзался в node.Nodes
        await foreach (var neighbor in node.Nodes)
            yield return neighbor;
    }
    async IAsyncEnumerable<CarrierNodeBacking> GetNeighbors(NodeRef path) {
        var node = await Get(path);
        if (node is null)
            yield break;
        await foreach (var neighbor in node.Nodes)
            yield return neighbor;
    }
    async Task Update(NodeRef path, IDictionary<string, string> attributes) {
        var node = await Get(path);
        if (node is null)
            return;
        node.Attributes = attributes.ToDictionary();
    }
    Task Delete(CarrierNodeBacking node);
    Task Delete(NodeRef path);
    Task Connect(NodeRef sourcePath, NodeRef targetPath);
    Task Disconnect(NodeRef sourcePath, NodeRef targetPath);
    IAsyncEnumerable<CarrierNodeBacking> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other);
    IAsyncEnumerable<CarrierNodeBacking> EnumerateNodesAsync(CancellationToken cancellationToken = default);
}
