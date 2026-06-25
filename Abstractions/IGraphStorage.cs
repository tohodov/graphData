namespace Abstractions;

internal interface IGraphStorage {
    NodeState Root { get; }

    Task<NodeState> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null);
    Task<NodeState?> Get(NodeRef path);
    async Task<NodeState?> Get(NodeRef parent, NodeLocalId nodeId) {
        var node = await Get(parent);
        if (node is null)
            return null;
        var matches = node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId && neighbor.LocalId == nodeId)
            .DistinctBy(static neighbor => neighbor.GlobalId)
            .Take(2)
            .ToArray();
        return matches.Length switch {
            0 => null,
            1 => matches[0],
            _ => throw new Exception("")
        };
    }
    async IAsyncEnumerable<NodeState> GetNeighbors(NodeState node) {//TODO вывернуть наоборот чтобы это метод юзался в node.Nodes
        foreach (var neighbor in node.Nodes)//TODO IAsyncEnumerable
            yield return neighbor;
    }
    async IAsyncEnumerable<NodeState> GetNeighbors(NodeRef path) {
        var node = await Get(path);
        if (node is null)
            yield break;
        foreach (var neighbor in node.Nodes)//TODO IAsyncEnumerable
            yield return neighbor;
    }
    async Task Update(NodeRef path, IDictionary<string, string> attributes) {
        var node = await Get(path);
        if (node is null)
            return;
        node.Attributes = attributes.ToDictionary();
    }
    Task Delete(NodeState node);
    Task Delete(NodeRef path);
    Task Connect(NodeRef sourcePath, NodeRef targetPath);
    Task Disconnect(NodeRef sourcePath, NodeRef targetPath);
    IAsyncEnumerable<NodeState> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other);
    IAsyncEnumerable<NodeState> EnumerateNodesAsync(CancellationToken cancellationToken = default);
}
