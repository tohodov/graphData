using Abstractions;

namespace GraphData.Core.Models;

public static class GraphBaseTypeIds
{
    public static NodeGlobalId NodeType { get; } = new("graphdata", "types", "nodes", "Type");
    public static NodeGlobalId NodeInstance { get; } = new("graphdata", "types", "nodes", "Instance");
    public static NodeGlobalId EdgeType { get; } = new("graphdata", "types", "edges", "Type");
    public static NodeGlobalId EdgeInstance { get; } = new("graphdata", "types", "edges", "Instance");
}
