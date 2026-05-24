namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required string[] SourcePath { get; init; }

    public required string[] TargetPath { get; init; }
}
