namespace SemanticStorage;

internal sealed record SemanticTypeFileRecord
{
    public int FormatVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyList<string> RequiredTypeIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed record SemanticInstanceFileRecord
{
    public int FormatVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyList<string> TypeIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SemanticRelationMemberFileRecord> Members { get; init; } =
        Array.Empty<SemanticRelationMemberFileRecord>();
}

internal sealed record SemanticRelationMemberFileRecord
{
    public required string Name { get; init; }

    public required string MemberTypeId { get; init; }

    public IReadOnlyList<string> ParticipantIds { get; init; } = Array.Empty<string>();
}

internal sealed record SemanticIncidentIndexFileRecord
{
    public int FormatVersion { get; init; } = 1;

    public required string ParticipantId { get; init; }

    public IReadOnlyList<SemanticIncidentIndexEntryFileRecord> Relations { get; init; } =
        Array.Empty<SemanticIncidentIndexEntryFileRecord>();
}

internal sealed record SemanticIncidentIndexEntryFileRecord
{
    public required string RelationId { get; init; }

    public required string MemberName { get; init; }

    public int MemberIndex { get; init; }
}
