namespace GraphData.Api.Models;

public sealed record EdgeResponse
{
    public required string SourceName { get; init; }

    public required string TargetName { get; init; }
}
