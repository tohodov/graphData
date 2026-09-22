using System.Text.Json.Serialization;

namespace GraphData.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TypedElementKindDto>))]
public enum TypedElementKindDto { Instance, Relation }

[JsonConverter(typeof(JsonStringEnumConverter<TypedScalarKindDto>))]
public enum TypedScalarKindDto { String, Boolean, Int32, Int64, Double, Decimal, Guid, DateTime }

public sealed record TypedMemberDefinitionDto
{
    public required string Name { get; init; }
    public string? TypeId { get; init; }
    public int Min { get; init; } = 1;
    public int? Max { get; init; } = 1;
}

public sealed record TypedAttributeDefinitionDto
{
    public required string Name { get; init; }
    public required TypedScalarKindDto Kind { get; init; }
    public bool Required { get; init; }
}

public sealed record TypedTypeRequest
{
    public required string Id { get; init; }
    public required TypedElementKindDto Kind { get; init; }
    public bool IsAbstract { get; init; }
    public string[] RequiredTypeIds { get; init; } = [];
    public TypedMemberDefinitionDto[] Members { get; init; } = [];
    public TypedAttributeDefinitionDto[] Attributes { get; init; } = [];
}

public sealed record TypedTypeResponse
{
    public required string Id { get; init; }
    public required TypedElementKindDto Kind { get; init; }
    public required bool IsAbstract { get; init; }
    public required string[] RequiredTypeIds { get; init; }
    public required TypedMemberDefinitionDto[] Members { get; init; }
    public required TypedAttributeDefinitionDto[] Attributes { get; init; }
}

public sealed record TypedInstanceRequest
{
    public required string Id { get; init; }
    public string[] TypeIds { get; init; } = [];
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.Ordinal);
}

public sealed record TypedRelationRequest
{
    public required string Id { get; init; }
    public required string TypeId { get; init; }
    public required Dictionary<string, string[]> Members { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.Ordinal);
}

public sealed record TypedAttributesRequest
{
    public required Dictionary<string, string> Attributes { get; init; }
}

public sealed record TypedElementResponse
{
    public required string Id { get; init; }
    public required TypedElementKindDto Kind { get; init; }
    public required string[] TypeIds { get; init; }
    public required Dictionary<string, string> Attributes { get; init; }
    public required Dictionary<string, string[]> Members { get; init; }
}

public sealed record TypedOperationResponse;
