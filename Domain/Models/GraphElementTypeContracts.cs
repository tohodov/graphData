namespace GraphData.Core.Models;

public interface IGraphNodeType
{
    static abstract NodeGlobalId StaticTypeId { get; }
}

public interface IGraphEdgeType
{
    static abstract NodeGlobalId StaticTypeId { get; }
}
