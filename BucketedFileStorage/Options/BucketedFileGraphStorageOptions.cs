namespace GraphData.BucketedFileStorage.Options;

public sealed class BucketedFileGraphStorageOptions
{
    private int _bucketPrefixLength = 2;

    public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "bucketed-file-storage");

    public string MetadataDirectoryName { get; set; } = "metadata-buckets";

    public string ConnectionsDirectoryName { get; set; } = "connection-buckets";

    public int BucketPrefixLength
    {
        get => _bucketPrefixLength;
        set
        {
            if (value < 1 || value > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Bucket prefix length must be between 1 and 64.");
            }

            _bucketPrefixLength = value;
        }
    }
}
