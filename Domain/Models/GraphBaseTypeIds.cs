using Abstractions;

namespace GraphData.Core.Models;

public static class GraphBaseTypeIds
{
    public static InternalId NodeTypeRoot => GraphSystemNodeIds.NodeTypeRoot;

    public static InternalId NodeType { get; } = new("graphdata", "types", "nodes", "Type");
    public static InternalId NodeInstance { get; } = new("graphdata", "types", "nodes", "Instance");
    public static InternalId Connection { get; } = new("graphdata", "types", "nodes", "Connection");
    public static InternalId Endpoint { get; } = new("graphdata", "types", "nodes", "Endpoint");
    public static InternalId Port { get; } = new("graphdata", "types", "nodes", "Port");
}
