namespace Abstractions;

internal interface IGraphStorage {
    NodeBacking Root { get; }

    Task<NodeBacking> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null);
    Task<NodeBacking?> Get(NodeRef path);
    async Task<NodeBacking?> Get(NodeRef parent, NodeLocalId nodeId) {
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
    async IAsyncEnumerable<NodeBacking> GetNeighbors(NodeBacking node) {//TODO вывернуть наоборот чтобы этот метод юзался в node.Nodes
        await foreach (var neighbor in node.Nodes)
            yield return neighbor;
    }
    async IAsyncEnumerable<NodeBacking> GetNeighbors(NodeRef path) {
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
    Task Delete(NodeBacking node);
    Task Delete(NodeRef path);
    Task Connect(NodeRef sourcePath, NodeRef targetPath);
    Task Disconnect(NodeRef sourcePath, NodeRef targetPath);
    IAsyncEnumerable<NodeBacking> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other);
    IAsyncEnumerable<NodeBacking> EnumerateNodesAsync(CancellationToken cancellationToken = default);
}
