using System.ComponentModel.DataAnnotations;

namespace GraphData.Api.Models;

public sealed class CreateNodeRequest
{
    public string? Name { get; init; }

    public Dictionary<string, string>? Attributes { get; init; }
}
