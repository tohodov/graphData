namespace GraphData.Core.Models;

public sealed record NodeDetails
{
    public required NodeMetadata Metadata { get; init; }

    public required IReadOnlyCollection<Guid> Connections { get; init; }
}
