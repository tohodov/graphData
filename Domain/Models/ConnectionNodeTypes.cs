using Abstractions;

namespace GraphData.Core.Models;

/// <summary>
/// Describes the semantic connection between a carrier node and one materialized
/// instance of a graph type. It is an ordinary graph type rather than a hidden
/// storage primitive.
/// </summary>
public sealed class InstanceOfEdge : Edge {
    public Node Instance = null!;
    public Node Type = null!;

    internal InstanceOfEdge(EdgeBacking state) : base(state) {
    }
}

/// <summary>
/// Describes graph-level multiple inheritance. A derived type requires every
/// facet of the required type to be materialized on its instances.
/// </summary>
public sealed class RequiresEdge : Edge {
    public Node Derived = null!;
    public Node Required = null!;

    internal RequiresEdge(EdgeBacking state) : base(state) {
    }
}
