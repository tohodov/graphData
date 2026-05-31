namespace GraphData.Api.Models;

public sealed class CreateNodeRequest
{
    public required string LocalId { get; init; }

    public string[]? ParentGlobalId { get; init; }

    public Dictionary<string, string>? Attributes { get; init; }
}
