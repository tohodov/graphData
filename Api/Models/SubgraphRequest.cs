using System;
using System.Collections.Generic;

namespace GraphData.Api.Models;

public sealed class SubgraphRequest
{
    public ICollection<string[]> RootPaths { get; set; } = Array.Empty<string[]>();

    public int MaxDepth { get; set; } = 1;

    public bool IncludeDisconnectedRoots { get; set; }
}
