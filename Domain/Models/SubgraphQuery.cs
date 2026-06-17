using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Abstractions;

namespace GraphData.Core.Models;

public sealed class SubgraphQuery {
    public required IReadOnlyCollection<NodeGlobalId> Nodes { get; init; }
    public int MaxDepth { get; init; }
}
