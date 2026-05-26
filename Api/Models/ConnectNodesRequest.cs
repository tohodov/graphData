using GraphData.Core.Models;

namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required NodeGlobalId SourcePath { get; init; }
    public required NodeGlobalId TargetPath { get; init; }
}
