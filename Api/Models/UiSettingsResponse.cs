namespace GraphData.Api.Models;

public sealed record UiSettingsResponse
{
    public required UiSystemNodeIdsResponse SystemNodeIds { get; init; }

    public required UiBaseTypeIdsResponse BaseTypeIds { get; init; }

    public required UiBasisResponse Basis { get; init; }
}

public sealed record UiSystemNodeIdsResponse
{
    public required string GraphDataRoot { get; init; }

    public required string TypeRoot { get; init; }

    public required string NodeTypeRoot { get; init; }

    public required string StorageRoot { get; init; }

    public required string InitializerRoot { get; init; }

    public required string RuntimeTypesInitializer { get; init; }
}

public sealed record UiBaseTypeIdsResponse
{
    public required string NodeType { get; init; }

    public required string NodeInstance { get; init; }

    // Compatibility name for UI code that still treats relation node types as edge projection types.
    public required string EdgeType { get; init; }

    public required string Relation { get; init; }

    public required string Endpoint { get; init; }

    public required string Port { get; init; }
}

public sealed record UiBasisResponse
{
    public required string NodeTypeRoot { get; init; }
}
