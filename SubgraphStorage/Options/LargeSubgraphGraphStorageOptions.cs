namespace GraphData.SubgraphStorage.Options;

public sealed class LargeSubgraphGraphStorageOptions
{
    private int _bucketPrefixLength = 2;

    public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "subgraph-storage");

    public string MetadataDirectoryName { get; set; } = "nodes";

    public string ConnectionsDirectoryName { get; set; } = "connections";

    public int BucketPrefixLength
    {
        get => _bucketPrefixLength;
        set
        {
            if (value < 1 || value > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Bucket prefix length must be between 1 and 32.");
            }

            _bucketPrefixLength = value;
        }
    }
}
