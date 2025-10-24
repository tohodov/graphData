using System.Text.Json.Serialization;

namespace GraphData.Core.Models;

public sealed record NodeMetadata
{
    public Guid Id { get; init; }

    public string? Name { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, string> Attributes { get; set; } = new();
}
