namespace GraphData.Api.Models;

public sealed record CreateRelationRequest
{
    public required string LocalId { get; init; }

    public required string[] TypeGlobalId { get; init; }

    /// <summary>
    /// Named relation members. Each value contains zero or more participant
    /// ids; every participant id is represented as path segments.
    /// </summary>
    public IReadOnlyDictionary<string, string[][]> Members { get; init; } =
        new Dictionary<string, string[][]>(StringComparer.Ordinal);
}
