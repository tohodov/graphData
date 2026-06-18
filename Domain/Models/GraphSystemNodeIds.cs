using Abstractions;

namespace GraphData.Core.Models;

public static class GraphSystemNodeIds
{
    public static NodeGlobalId GraphDataRoot { get; } = new("graphdata");
    public static NodeGlobalId TypeRoot { get; } = new("graphdata", "types");
    public static NodeGlobalId NodeTypeRoot { get; } = new("graphdata", "types", "nodes");
    public static NodeGlobalId EdgeTypeRoot { get; } = new("graphdata", "types", "edges");
    public static NodeGlobalId RelationRoot { get; } = new("graphdata", "relations");
    public static NodeGlobalId StorageRoot { get; } = new("graphdata", "storage");
    public static NodeGlobalId InitializerRoot { get; } = new("graphdata", "storage", "initializers");
    public static NodeGlobalId RuntimeTypesInitializer { get; } = new("graphdata", "storage", "initializers", "runtime-types");
}
