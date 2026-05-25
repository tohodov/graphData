namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required NodePath SourcePath { get; init; }
    public required NodePath TargetPath { get; init; }
}
