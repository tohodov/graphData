using Abstractions;

namespace GraphData.Core.Models;

public static class GraphBaseTypeIds
{
    public static InternalId NodeTypeRoot => GraphSystemNodeIds.NodeTypeRoot;
    public static InternalId EdgeTypeRoot => GraphSystemNodeIds.EdgeTypeRoot;

    public static InternalId NodeType { get; } = new("graphdata", "types", "nodes", "Type");
    public static InternalId NodeInstance { get; } = new("graphdata", "types", "nodes", "Instance");
    public static InternalId EdgeType { get; } = new("graphdata", "types", "edges", "Type");
    public static InternalId EdgeInstance { get; } = new("graphdata", "types", "edges", "Instance");
}
