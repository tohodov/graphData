namespace GraphData.Api.Models;

public sealed record RelationMemberResponse
{
    public required string Name { get; init; }

    public required string MemberTypeId { get; init; }

    public IReadOnlyList<string> ParticipantIds { get; init; } = Array.Empty<string>();
}
