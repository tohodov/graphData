namespace GraphData.Api.Models;

public sealed record UiSettingsResponse
{
    public required UiSystemNodeIdsResponse SystemNodeIds { get; init; }
}

public sealed record UiSystemNodeIdsResponse
{
    public required string NodeTypeRoot { get; init; }
    public required string StorageRoot { get; init; }
}
