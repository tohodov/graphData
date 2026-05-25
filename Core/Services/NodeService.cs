using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class NodeService(IGraphStorage storage) {
    private readonly IGraphStorage _storage = storage;

    public async Task<Node?> Get(NodePath path) {
        var node = await _storage.Get(path);
        if (node is null)
            return null;
        return node;
    }

    public async Task<(Node Node, IReadOnlyCollection<Node> Connections)?> GetNeighborhood(NodePath path) {
        var node = await _storage.Get(path);
        if (node is null)
            return null;
        var connections = await _storage.GetConnectedNodesAsync(node);
        return (node, connections);
    }

    public async Task<Node> Create(Node? parent, string name, Dictionary<string, string>? attributes = null) {
        NodeNameValidator.Validate(name, nameof(name));
        return await _storage.Create(name, parent, attributes);
    }

    public Task Update(NodePath path, IDictionary<string, string> attributes) {
        return _storage.Update(path, attributes);
    }

    public Task Delete(NodePath path) {
        return _storage.Delete(path);
    }

    public Task ConnectNodes(Node first, Node second) {
        if (first == second)
            throw new ArgumentException("Node id must be provided.", nameof(second));
        return _storage.Connect(first, second);
    }

    public Task<Subgraph> GetSubgraph(SubgraphQuery query) {
        if (query.Nodes.Count == 0)
            return Task.FromResult(Subgraph.Empty);
        if (query.MaxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(query.MaxDepth), "Depth must be non-negative.");
        return _storage.GetSubgraphAsync(query);
    }
}
