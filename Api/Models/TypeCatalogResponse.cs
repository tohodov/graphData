namespace GraphData.Api.Models;

public sealed record TypeCatalogResponse
{
    public IReadOnlyCollection<NodeTypeDefinitionResponse> Types { get; init; } =
        Array.Empty<NodeTypeDefinitionResponse>();

    public IReadOnlyCollection<TypeReferenceResponse> References { get; init; } =
        Array.Empty<TypeReferenceResponse>();
}

public sealed record NodeTypeDefinitionResponse
{
    public required string LocalId { get; init; }

    public required string InternalId { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>();

    public bool IsAbstract { get; init; }

    public IReadOnlyCollection<NodeTypeFieldResponse> Fields { get; init; } =
        Array.Empty<NodeTypeFieldResponse>();

    public IReadOnlyCollection<NodeTypeSlotResponse> Slots { get; init; } =
        Array.Empty<NodeTypeSlotResponse>();
}

public sealed record NodeTypeFieldResponse
{
    public required string Name { get; init; }

    public required string ValueKind { get; init; }

    public required string ClrType { get; init; }

    public required CardinalityResponse Cardinality { get; init; }

    public bool IsCollection { get; init; }

    public string? NodeTypeInternalId { get; init; }
}

public sealed record NodeTypeSlotResponse
{
    public required string Name { get; init; }

    public required CardinalityResponse Cardinality { get; init; }

    public IReadOnlyCollection<string> AllowedTypeInternalIds { get; init; } =
        Array.Empty<string>();
}

public sealed record CardinalityResponse
{
    public int Min { get; init; }

    public int? Max { get; init; }

    public required string Text { get; init; }
}

public sealed record TypeReferenceResponse
{
    public required string Kind { get; init; }

    public required string SourceTypeInternalId { get; init; }

    public required string MemberKind { get; init; }

    public required string MemberName { get; init; }

    public required string TargetTypeInternalId { get; init; }
}
