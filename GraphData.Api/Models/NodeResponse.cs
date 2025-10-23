namespace GraphData.Api.Models;

public sealed record NodeResponse
{
    public required Guid Id { get; init; }

    public string? Name { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public IReadOnlyCollection<Guid> Connections { get; init; } = Array.Empty<Guid>();
}
