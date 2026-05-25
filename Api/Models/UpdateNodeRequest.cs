namespace GraphData.Api.Models;

public sealed class UpdateNodeRequest
{
    public required Dictionary<string, string> Attributes { get; init; }
}
