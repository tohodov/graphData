namespace GraphData.PerNodeFileStorage.Options;

public sealed class PerNodeFileGraphStorageOptions
{
    public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "per-node-file-storage");

    public string MetadataDirectoryName { get; set; } = "metadata";

    public string ConnectionsDirectoryName { get; set; } = "connections";

    public string MetadataFileExtension { get; set; } = ".json";

    public string ConnectionsFileExtension { get; set; } = ".json";
}
