using Abstractions;

namespace GraphData.Core.Models;

public static class GraphSystemNodeIds
{
    public static InternalId GraphDataRoot { get; } = new("graphdata");
    public static InternalId TypeRoot { get; } = new("graphdata", "types");
    public static InternalId NodeTypeRoot { get; } = new("graphdata", "types", "nodes");
    public static InternalId StorageRoot { get; } = new("graphdata", "storage");
    public static InternalId InitializerRoot { get; } = new("graphdata", "storage", "initializers");
    public static InternalId RuntimeTypesInitializer { get; } = new("graphdata", "storage", "initializers", "runtime-types");
}
