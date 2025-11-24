namespace SymLinkStorage;

public sealed class NtfsGraphStorageOptions
{
    public string RootPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "graph-data");

    public string MetadataFileName { get; init; } = "node.json";
}
