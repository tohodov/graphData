namespace GraphData.Api.Models;

public sealed record NodeResponse
{
    public required string LocalId { get; init; }

    public required string InternalId { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public required string SemanticKind { get; init; }

    public IReadOnlyCollection<string> TypeIds { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<RelationMemberResponse> RelationMembers { get; init; } = Array.Empty<RelationMemberResponse>();

    public IReadOnlyCollection<EdgeResponse> Edges { get; init; } = Array.Empty<EdgeResponse>();
}
