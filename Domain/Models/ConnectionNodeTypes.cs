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
