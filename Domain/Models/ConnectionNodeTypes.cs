namespace GraphData.Core.Models;

public sealed class ConnectionNodeType : NodeType
{
    public override void Define(NodeTypeBuilder type)
    {
        type.Abstract();
    }
}

public sealed class EndpointNodeType : NodeType
{
}

public sealed class PortNodeType : NodeType
{
}
