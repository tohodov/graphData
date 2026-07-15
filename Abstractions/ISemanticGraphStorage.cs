namespace Abstractions;

/// <summary>
/// Canonical semantic element persisted by the storage. The simple graph
/// exposed through <see cref="IGraphStorage"/> is only a compatibility
/// projection of these elements.
/// </summary>
internal enum SemanticElementKind
{
    Root,
    TypeCatalog,
    Type,
    TypeInfrastructure,
    Instance,
    Relation,
    RelationMember
}

internal sealed record SemanticRelationMember(
    string Name,
    InternalId MemberTypeId,
    IReadOnlyList<InternalId> ParticipantIds);

internal sealed record SemanticRelation(
    InternalId Id,
    IReadOnlyList<InternalId> TypeIds,
    IReadOnlyList<SemanticRelationMember> Members);

/// <summary>
/// One atomic read of a canonical semantic record. It prevents consumers such
/// as backup from combining fields read from different record revisions.
/// </summary>
internal sealed record SemanticElementSnapshot(
    InternalId Id,
    SemanticElementKind Kind,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<InternalId> TypeIds,
    IReadOnlyList<InternalId> RequiredTypeIds,
    IReadOnlyList<SemanticRelationMember> Members);

/// <summary>
/// Semantic information carried by a backing returned from the compatibility
/// projection. Implementations must obtain it from canonical semantic records,
/// never infer it from projected edges.
/// </summary>
internal interface ISemanticNodeBacking
{
    SemanticElementKind SemanticKind { get; }
    IReadOnlyCollection<InternalId> DeclaredTypeIds { get; }
    IReadOnlyCollection<SemanticRelationMember> RelationMembers { get; }
}

/// <summary>
/// Canonical storage contract for the typed instance graph and the separate
/// type catalog. Type references are deliberately weak: assigning an id does
/// not require the referenced type record to be available.
/// </summary>
internal interface ISemanticGraphStorage
{
    NodeBacking TypeRoot { get; }

    bool IsType(NodeBacking node);
    IReadOnlyCollection<InternalId> GetDeclaredTypeIds(NodeBacking node);
    IReadOnlyCollection<InternalId> GetRequiredTypeIds(NodeBacking type);

    Task SetDeclaredTypeIdsAsync(NodeRef node, IEnumerable<NodeRef> typeIds);
    Task SetRequiredTypeIdsAsync(NodeRef type, IEnumerable<NodeRef> requiredTypeIds);

    Task<NodeBacking> CreateTypeElementAsync(
        NodeLocalId localId,
        NodeRef parentId,
        SemanticElementKind kind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes = null);

    Task<NodeBacking> ReplaceTypeElementAsync(
        NodeRef typeId,
        SemanticElementKind expectedKind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes = null);

    Task<NodeBacking> CreateInstanceAsync(
        NodeLocalId localId,
        NodeRef? namespacePrefix,
        IReadOnlyCollection<NodeRef> typeIds,
        IDictionary<string, string>? attributes = null);

    Task<NodeBacking> ReplaceInstanceAsync(
        NodeRef instanceId,
        IReadOnlyCollection<NodeRef> typeIds,
        IDictionary<string, string>? attributes = null);

    Task<NodeBacking> CreateRelationAsync(
        NodeLocalId localId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes = null);

    Task<NodeBacking> CreateRelationAsync(
        NodeLocalId localId,
        NodeRef typeId,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes = null) =>
        CreateRelationAsync(localId, new[] { typeId }, members, attributes);

    /// <summary>
    /// Atomically replaces the canonical contents of an existing relation
    /// without changing its stable id or deleting higher-order dependents.
    /// </summary>
    Task<NodeBacking> ReplaceRelationAsync(
        NodeRef relationId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes = null);

    Task<SemanticRelation?> GetRelationAsync(NodeRef relationId);

    Task<SemanticElementSnapshot?> ReadSemanticElementAsync(
        NodeRef elementId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SemanticRelation> FindIncidentRelationsAsync(
        NodeRef participantId,
        NodeRef? relationTypeId = null,
        string? memberName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates canonical type, instance, and relation records. It must not
    /// return virtual compatibility nodes and must include hidden untyped
    /// relation records.
    /// </summary>
    IAsyncEnumerable<NodeBacking> EnumerateSemanticElementsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the canonical type catalog only, including its root and
    /// definition infrastructure. This is an explicit catalog operation and
    /// must not scan instance records.
    /// </summary>
    IAsyncEnumerable<NodeBacking> EnumerateTypeElementsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the nearest canonical instance records reachable below an address
    /// prefix. Empty namespace directories are transparent. This is namespace
    /// discovery only and does not imply graph adjacency.
    /// </summary>
    IAsyncEnumerable<NodeBacking> EnumerateInstanceNamespaceChildrenAsync(
        NodeRef? parent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds disposable lookup indexes from canonical records. Normal reads
    /// and mutations must not call this global repair operation implicitly.
    /// </summary>
    Task RepairDerivedIndexesAsync(CancellationToken cancellationToken = default);
}
