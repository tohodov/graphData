namespace SemanticStorage;

public sealed class SemanticGraphStorageOptions
{
    public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "semantic-graph-data");

    public string TypesDirectoryName { get; init; } = "types";

    public string InstancesDirectoryName { get; init; } = "instances";

    public string IndexesDirectoryName { get; init; } = "indexes";
}
