using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GraphData.Core.Models;

public sealed class SubgraphQuery
{
    private IReadOnlyCollection<Guid> _rootNodeIds = Array.Empty<Guid>();

    public IReadOnlyCollection<Guid> RootNodeIds
    {
        get => _rootNodeIds;
        init => _rootNodeIds = value is null
            ? throw new ArgumentNullException(nameof(value))
            : new ReadOnlyCollection<Guid>(value.ToArray());
    }

    public int MaxDepth { get; init; }

    public bool IncludeDisconnectedRoots { get; init; }

    public static SubgraphQuery FromRoots(IEnumerable<Guid> roots, int maxDepth = 1, bool includeDisconnectedRoots = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return new SubgraphQuery
        {
            RootNodeIds = roots.ToArray(),
            MaxDepth = maxDepth,
            IncludeDisconnectedRoots = includeDisconnectedRoots
        };
    }
}
