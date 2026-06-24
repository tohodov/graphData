using Abstractions;

namespace GraphData.Core.Models;

public sealed class ConnectionNodeType : NodeType //TODO удалить
{
    internal ConnectionNodeType(NodeState state) : base(state) {
    }
}

public sealed class EndpointNodeType : NodeType {
    internal EndpointNodeType(NodeState state) : base(state) {
    }
}

public sealed class PortNodeType : NodeType {
    internal PortNodeType(NodeState state) : base(state) {
    }
}
