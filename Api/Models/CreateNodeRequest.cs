namespace GraphData.Api.Models;

public sealed class CreateNodeRequest
{
    public required string Name { get; init; }

    public string? ParentName { get; init; }

    public Dictionary<string, string>? Attributes { get; init; }
}
