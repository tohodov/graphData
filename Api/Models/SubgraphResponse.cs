using System;
using System.Collections.Generic;

namespace GraphData.Api.Models;

public sealed class SubgraphResponse
{
    public ICollection<NodeResponse> Nodes { get; set; } = Array.Empty<NodeResponse>();
}
