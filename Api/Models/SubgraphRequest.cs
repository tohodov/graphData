using System;
using System.Collections.Generic;

namespace GraphData.Api.Models;

public sealed class SubgraphRequest
{
    public ICollection<Guid> RootNodeIds { get; set; } = Array.Empty<Guid>();

    public int MaxDepth { get; set; } = 1;

    public bool IncludeDisconnectedRoots { get; set; }
}
