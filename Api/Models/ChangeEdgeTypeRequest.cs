namespace GraphData.Api.Models;

public sealed class ChangeEdgeTypeRequest {
    [Obsolete("удалить", true)]
    public string[]? TypedEdgeGlobalId { get; init; }//TODO удалить

    [Obsolete("удалить", true)]
    public string[]? RelationGlobalId { get; init; }//TODO удалить

    public required string[] Node1InternalId { get; init; }

    public required string[] Node2InternalId { get; init; }

    public required string[] TypeGlobalId { get; init; }

    [Obsolete("удалить", true)]
    public string[]? TypedEdgeParentGlobalId { get; init; }//TODO удалить

    [Obsolete("удалить", true)]
    public string[]? RelationParentGlobalId { get; init; }//TODO удалить

    [Obsolete("удалить", true)]
    public string? TypedEdgeLocalId { get; init; }//TODO удалить

    [Obsolete("удалить", true)]
    public string? RelationLocalId { get; init; }//TODO удалить
}
