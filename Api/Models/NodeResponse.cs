namespace GraphData.Api.Models;

public sealed record NodeResponse
{
    public required string Name { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}
