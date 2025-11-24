using System;
using System.Collections.Generic;

namespace GraphData.Core.Models;

public sealed class Subgraph
{
    public static Subgraph Empty { get; } = new Subgraph { Nodes = [] };
    public required IReadOnlyCollection<Node> Nodes { get; init; }
}
