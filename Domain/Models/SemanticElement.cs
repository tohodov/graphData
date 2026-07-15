namespace GraphData.Core.Models;

/// <summary>
/// Public semantic shape of a stored element. Address hierarchy and member
/// ports from the raw graph adapter are projection details and are not kinds.
/// </summary>
public enum GraphElementStorageKind
{
    Unknown,
    Type,
    Instance,
    Relation
}

public sealed record RelationMember(
    string Name,
    InternalId MemberTypeId,
    IReadOnlyList<InternalId> ParticipantIds);
