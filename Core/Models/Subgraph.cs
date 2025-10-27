using System;
using System.Collections.Generic;

namespace GraphData.Core.Models;

public sealed class Subgraph
{
    public Subgraph(IReadOnlyDictionary<Guid, NodeDetails> nodes)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }

    public IReadOnlyDictionary<Guid, NodeDetails> Nodes { get; }

    public static Subgraph Empty { get; } = new Subgraph(new Dictionary<Guid, NodeDetails>());
}
