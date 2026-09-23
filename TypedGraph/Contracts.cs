namespace GraphData.Typed;

public enum TypedElementKind { Instance, Relation }
public enum ScalarKind { String, Boolean, Int32, Int64, Double, Decimal, Guid, DateTime }

public sealed record TypedMemberDefinition(string Name, string? TypeId, int Min = 1, int? Max = 1);
public sealed record TypedAttributeDefinition(string Name, ScalarKind Kind, bool Required = false);

/// <summary>A type declaration in the selected catalog; no storage nodes or CLR types escape here.</summary>
public sealed record TypedType(
    string Id,
    TypedElementKind Kind,
    bool IsAbstract,
    IReadOnlyList<string> RequiredTypeIds,
    IReadOnlyList<TypedMemberDefinition> Members,
    IReadOnlyList<TypedAttributeDefinition> Attributes);

/// <summary>TypeIds are materialized effective types, not a claim about assignment provenance.
/// Members are unordered sets of participants, keyed by role. An ID's spelling never implies a semantic edge.</summary>
public sealed record TypedElement(
    string Id,
    TypedElementKind Kind,
    IReadOnlyList<string> TypeIds,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Members);

public interface ITypedGraph
{
    Task<TypedType> GetTypeAsync(string id);
    Task<IReadOnlyList<TypedType>> GetTypesAsync();
    Task<TypedElement> GetAsync(string id);
    Task<IReadOnlyList<TypedElement>> GetElementsAsync();
    Task<IReadOnlyList<TypedElement>> FindIncidentRelationsAsync(string participantId);
    Task<TypedType> CreateTypeAsync(TypedType definition);
    Task<TypedElement> CreateInstanceAsync(string id, IReadOnlyList<string> typeIds, IReadOnlyDictionary<string, string> attributes);
    Task<TypedElement> CreateRelationAsync(string id, string? typeId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, IReadOnlyDictionary<string, string> attributes);
    Task<TypedElement> ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes);
    /// <summary>Replace assigned types, materializing requirements and preserving identity. No types means no schema.</summary>
    Task<TypedElement> ReplaceTypesAsync(string id, IReadOnlyList<string> typeIds);
    /// <summary>Replace a relation's participants without recreating the fact or its incoming references.</summary>
    Task<TypedElement> ReplaceMembersAsync(string id, IReadOnlyDictionary<string, IReadOnlyList<string>> members);
    /// <summary>Atomically change a relation's schema, members and attributes while retaining the fact's identity.</summary>
    Task<TypedElement> ReplaceRelationAsync(string id, string? typeId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, IReadOnlyDictionary<string, string> attributes);
    /// <summary>Restrict deletion: reject referenced elements and any backend-specific unrepresented data.</summary>
    Task DeleteAsync(string id);
}

/// <summary>Extension point for physical representations. Called through TypedGraphService.
/// Reads return detached snapshots, null means absent only. Malformed or ambiguous data must throw.
/// Inserts are create-only, must check collisions, and must reject unsupported requests before writing.
/// Implementations must not invent a raw graph projection. No implicit migration or repair is allowed.</summary>
public interface ITypedGraphStore
{
    Task<TypedType?> ReadTypeAsync(string id);
    Task<IReadOnlyList<TypedType>> ReadTypesAsync();
    Task<TypedElement?> ReadElementAsync(string id);
    Task<IReadOnlyList<TypedElement>> ReadElementsAsync();
    Task<IReadOnlyList<TypedElement>> ReadIncidentRelationsAsync(string participantId);
    Task InsertTypeAsync(TypedType definition);
    Task InsertElementAsync(TypedElement element);
    Task ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes);
    Task ReplaceTypesAsync(string id, IReadOnlyList<string> typeIds);
    Task ReplaceMembersAsync(string id, IReadOnlyDictionary<string, IReadOnlyList<string>> members);
    Task ReplaceElementAsync(TypedElement element);
    Task DeleteElementAsync(string id);
}

public enum TypedGraphError { NotFound, Conflict, Invalid, Unsupported, Corrupt }

public sealed class TypedGraphException(TypedGraphError error, string message) : InvalidOperationException(message)
{
    public TypedGraphError Error { get; } = error;
}
