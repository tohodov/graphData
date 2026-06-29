using Abstractions;

public sealed class Graph {
    internal Graph(NodeBacking root, NodeBacking nodeTypes) {
        Root = new StorageRoot(root);
        NodeTypes = new NodeType(nodeTypes);
    }

    public Node Root { get; }
    public NodeType NodeTypes { get; }

    private sealed class StorageRoot(NodeBacking state) : NodeType(state) {
    }
}
