using System;
using System.Collections.Generic;

namespace GraphData.Core.Models;

public sealed class CarrierSubgraph
{
    public static CarrierSubgraph Empty { get; } = new CarrierSubgraph { Nodes = [] };
    public required IReadOnlyCollection<CarrierNode> Nodes { get; init; }
}
