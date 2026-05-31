namespace GraphData.Api.Models;

public sealed record NodeResponse
{
    public required string LocalId { get; init; }

    public required string GlobalId { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public IReadOnlyCollection<EdgeResponse> Edges { get; init; } = Array.Empty<EdgeResponse>();
}
