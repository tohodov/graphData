using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<Node> Create(string name, Node? parent = null, IDictionary<string, string>? attributes = null);
    Task<Node?> Get(string basisNodeName) => Get(null, basisNodeName);
    Task<Node?> Get(Node? parent, string subNodeName);
    Task<Node?> Get(NodePath path);
    async Task Update(NodePath path, IDictionary<string, string> attributes) {
        var node = await Get(path);
        if (node != null)
            node.Attributes = attributes.ToDictionary();
    }
    Task Delete(NodePath path);
    Task Connect(Node sourceNode, Node targetNode);
    Task Disconnect(Node sourceNode, Node targetNode);
    Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node);
    async Task<Subgraph> GetSubgraphAsync(SubgraphQuery query) { //TODO переосмыслить
        var comparer = StringComparer.OrdinalIgnoreCase;
        var visited = new HashSet<NodePath>();//TODO хэш тут надо проверить
        var discovered = new HashSet<NodePath>(query.Nodes);//TODO хэш тут надо проверить
        var queue = new Queue<(NodePath NodeId, int Depth)>();

        foreach (var root in query.Nodes)
            queue.Enqueue((root, 0));

        var nodes = new Dictionary<NodePath, Node>();//TODO хэш тут надо проверить

        while (queue.Count > 0) {
            //cancellationTokens.Token.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path))
                continue;
            var node = await Get(path);
            if (node == null)
                continue;
            nodes[path] = node;
            if (depth >= query.MaxDepth)
                continue;
            foreach (var neighborId in node.Nodes.Select(x => x.GlobalId))
                if (!neighborId.SequenceEqual(path) && discovered.Add(neighborId))
                    queue.Enqueue((neighborId, depth + 1));
        }

        return nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            };
    }
}
