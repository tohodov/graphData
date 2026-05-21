namespace GraphData.Api.Models;

public sealed class UpdateNodeRequest
{
    public Dictionary<string, string>? Attributes { get; init; }
}
