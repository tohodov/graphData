using Abstractions;
using GraphData.Core.Models;

public class NodeType : Node {
    internal NodeType(NodeBacking state)
        : base(state) {
    }
    public NodeType(NodeLocalId id) : this(new VirtualNodeState(id)) { }

    public static NodeLocalId CreateDefaultLocalId(Type type) {
        var name = type.Name;
        if (name.EndsWith(nameof(NodeType), StringComparison.Ordinal))
            name = name[..^nameof(NodeType).Length];
        else if (name.EndsWith(nameof(Node), StringComparison.Ordinal))
            name = name[..^nameof(Node).Length];

        return string.IsNullOrWhiteSpace(name) ? type.Name : name;
    }
}

public sealed class InstanceNode : NodeType {
    public NodeType Type { get; }

    internal InstanceNode(NodeLocalId id, NodeType type) : base(new VirtualNodeState(id)) {
        Type = type;
        AttachType();
    }
    internal InstanceNode(NodeBacking state, NodeType type) : base(state) {
        Type = type;
        AttachType();
    }

    private void AttachType() {
        _ = new InstanceOf(new EdgeStateReferenced(Backing, Type.Backing), this, Type);
    }
}

public class StorageRoot : NodeType {
    internal StorageRoot() : base(new VirtualNodeState(new NodeLocalId())) { }
    internal StorageRoot(NodeBacking state) : base(state) { }
}
