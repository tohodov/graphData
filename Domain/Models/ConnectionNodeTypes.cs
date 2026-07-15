using Abstractions;

namespace GraphData.Core.Models;

/// <summary>
/// Legacy CLR shape. Type assignment is now a weak TypeRef in instance metadata,
/// so this class is excluded from runtime type discovery.
/// </summary>
internal sealed class InstanceOfEdge : Edge {
    public Node Instance = null!;
    public Node Type = null!;

    internal InstanceOfEdge(EdgeBacking state) : base(state) {
    }
}

/// <summary>
/// Legacy CLR shape. Multiple inheritance is persisted as requiredTypeIds on a
/// type record and is not materialized as relations in the instance graph.
/// </summary>
internal sealed class RequiresEdge : Edge {
    public Node Derived = null!;
    public Node Required = null!;

    internal RequiresEdge(EdgeBacking state) : base(state) {
    }
}
