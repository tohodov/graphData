namespace GraphData.SubgraphStorage.Options;

public sealed class RandomAccessGraphStorageOptions
{
    public string RootPath { get; set; } = string.Empty;

    public string MetadataDirectoryName { get; set; } = "metadata";

    public string ConnectionsDirectoryName { get; set; } = "connections";

    public string MetadataFileExtension { get; set; } = ".json";

    public string ConnectionsFileExtension { get; set; } = ".json";
}
