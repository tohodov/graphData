using Abstractions;

namespace GraphData.Core.Models;

public sealed class ConnectionNodeType : NodeType //TODO удалить
{
    internal ConnectionNodeType(NodeBacking state) : base(state) {
    }
}

public sealed class EndpointNodeType : NodeType {
    internal EndpointNodeType(NodeBacking state) : base(state) {
    }
}

public sealed class PortNodeType : NodeType {
    internal PortNodeType(NodeBacking state) : base(state) {
    }
}

/// <summary>
/// Describes the semantic connection between a carrier node and one materialized
/// instance of a graph type. It is an ordinary graph type rather than a hidden
/// storage primitive.
/// </summary>
public sealed class InstanceOfConnectionNodeType : NodeType {
    public Node Instance = null!;
    public Node Type = null!;

    internal InstanceOfConnectionNodeType(NodeBacking state) : base(state) {
    }
}

/// <summary>
/// Describes graph-level multiple inheritance. A derived type requires every
/// facet of the required type to be materialized on its instances.
/// </summary>
public sealed class RequiresConnectionNodeType : NodeType {
    public Node Derived = null!;
    public Node Required = null!;

    internal RequiresConnectionNodeType(NodeBacking state) : base(state) {
    }
}
