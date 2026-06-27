namespace GraphData.Api.Models;

public sealed record UiSettingsResponse
{
    public required UiSystemNodeIdsResponse SystemNodeIds { get; init; }

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

public sealed record UiBasisResponse
{
    public required string NodeTypeRoot { get; init; }
}
