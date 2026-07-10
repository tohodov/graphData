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
        else if (name.EndsWith(nameof(Edge), StringComparison.Ordinal))
            name = name[..^nameof(Edge).Length];
        else if (name.EndsWith(nameof(Node), StringComparison.Ordinal))
            name = name[..^nameof(Node).Length];

        return string.IsNullOrWhiteSpace(name) ? type.Name : name;
    }
}

public sealed record NodeTypeInstance(NodeType Type, Node? Witness = null) {
    public bool IsMaterialized => Witness is not null;
}

public sealed class InstanceNode : Node {
    private readonly IReadOnlyCollection<NodeTypeInstance> typeInstances;

    public IReadOnlyCollection<NodeTypeInstance> TypeInstances => typeInstances;

    public IReadOnlyCollection<NodeType> AssignedTypes => typeInstances
        .Select(static instance => instance.Type)
        .DistinctBy(static type => type.GlobalId)
        .ToArray();

    /// <summary>
    /// Compatibility view for callers that still expect exactly one type.
    /// Multiple inheritance must be consumed through <see cref="AssignedTypes"/>.
    /// </summary>
    public NodeType Type => AssignedTypes.Count switch {
        1 => AssignedTypes.Single(),
        0 => throw new InvalidOperationException($"Node '{GlobalId}' has no assigned graph type."),
        _ => throw new InvalidOperationException(
            $"Node '{GlobalId}' has multiple assigned graph types; use {nameof(AssignedTypes)} instead of {nameof(Type)}.")
    };

    internal InstanceNode(NodeLocalId id, NodeType type)
        : this(new VirtualNodeState(id), [new NodeTypeInstance(type)]) {
    }

    internal InstanceNode(NodeBacking state, NodeType type)
        : this(state, [new NodeTypeInstance(type)]) {
    }

    internal InstanceNode(NodeBacking state, IEnumerable<NodeTypeInstance> typeInstances)
        : base(state) {
        this.typeInstances = typeInstances
            .GroupBy(static instance => instance.Type.GlobalId)
            .Select(static group => group.OrderByDescending(static instance => instance.IsMaterialized).First())
            .ToArray();

        foreach (var typeInstance in this.typeInstances)
            _ = new InstanceOf(
                new InMemoryEdgeBacking(Backing, typeInstance.Type.Backing),
                this,
                typeInstance.Type,
                typeInstance.Witness);
    }
}
