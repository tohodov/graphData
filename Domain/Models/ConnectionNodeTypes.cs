using Abstractions;

namespace GraphData.Core.Models;

/// <summary>
/// Describes the semantic connection between a carrier node and one materialized
/// instance of a graph type. It is an ordinary graph type rather than a hidden
/// storage primitive.
/// </summary>
public sealed class InstanceOfEdge : CarrierEdge {
    public CarrierNode Instance = null!;
    public CarrierNode Type = null!;

    internal InstanceOfEdge(CarrierEdgeBacking state) : base(state) {
    }
}

/// <summary>
/// Describes graph-level multiple inheritance. A derived type requires every
/// facet of the required type to be materialized on its instances.
/// </summary>
public sealed class RequiresEdge : CarrierEdge {
    public CarrierNode Derived = null!;
    public CarrierNode Required = null!;

    internal RequiresEdge(CarrierEdgeBacking state) : base(state) {
    }
}
