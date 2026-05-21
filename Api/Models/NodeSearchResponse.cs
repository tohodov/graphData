using System;
using System.Collections.Generic;

namespace GraphData.Api.Models;

public sealed class NodeSearchResponse
{
    public ICollection<NodeSearchMatchResponse> Matches { get; set; } = Array.Empty<NodeSearchMatchResponse>();
}

public sealed class NodeSearchMatchResponse
{
    public required NodeResponse Node { get; init; }

    public IReadOnlyDictionary<string, NodeResponse> Bindings { get; init; } =
        new Dictionary<string, NodeResponse>(StringComparer.OrdinalIgnoreCase);

    public double Score { get; init; }

    public IReadOnlyCollection<string> MatchedBy { get; init; } = Array.Empty<string>();
}
