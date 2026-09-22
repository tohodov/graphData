using GraphData.Compiler.Model;
using GraphData.Core.Models;

namespace GraphData.Compiler.Adapters;

/// <summary>
/// Explicit lowering for the raw carrier graph. Raw edges are undirected, so
/// every unique edge is emitted as two directed relations with weight 1.
/// </summary>
public static class RawSubgraphSnapshotAdapter
{
    public static TypedGraphSnapshot Create(
        Subgraph subgraph,
        Func<global::Node, IReadOnlyList<float>> featureSelector,
        string nodeTypeKey = "raw-node",
        string relationTypeKey = "raw-edge")
    {
        ArgumentNullException.ThrowIfNull(subgraph);
        ArgumentNullException.ThrowIfNull(featureSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeTypeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationTypeKey);

#pragma warning disable CS0618 // GlobalId is used only as a raw snapshot locator and deduplication key.
        var nodeGroups = subgraph.Nodes
            .GroupBy(static node => node.GlobalId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var duplicateNodeKeys = nodeGroups
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (duplicateNodeKeys.Length > 0)
            throw new InvalidOperationException(
                "Raw subgraph contains more than one node wrapper for GlobalId(s): "
                + string.Join(", ", duplicateNodeKeys.Select(static key => $"'{key}'"))
                + ".");

        var nodesByKey = nodeGroups
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.Ordinal);
        var orderedNodes = nodesByKey
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        var nodes = orderedNodes
            .Select(pair => new TypedNodeSnapshot(
                pair.Key,
                [nodeTypeKey],
                featureSelector(pair.Value).ToArray()))
            .ToArray();

        var uniqueEdges = new HashSet<(string First, string Second)>();
        foreach (var (_, node) in orderedNodes)
        {
            foreach (var edge in node.Edges)
            {
                var first = edge.Node1.GlobalId.ToString();
                var second = edge.Node2.GlobalId.ToString();
                if (!nodesByKey.ContainsKey(first) || !nodesByKey.ContainsKey(second))
                    continue;
                if (string.Equals(first, second, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Raw self-loop '{first}' cannot be lowered unambiguously.");
                if (StringComparer.Ordinal.Compare(first, second) > 0)
                    (first, second) = (second, first);
                uniqueEdges.Add((first, second));
            }
        }
#pragma warning restore CS0618

        var relations = new List<TypedRelationSnapshot>(uniqueEdges.Count * 2);
        var edgeIndex = 0;
        foreach (var edge in uniqueEdges
            .OrderBy(static edge => edge.First, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Second, StringComparer.Ordinal))
        {
            relations.Add(new TypedRelationSnapshot(
                $"{relationTypeKey}/{edgeIndex}/forward",
                relationTypeKey,
                edge.First,
                edge.Second));
            relations.Add(new TypedRelationSnapshot(
                $"{relationTypeKey}/{edgeIndex}/reverse",
                relationTypeKey,
                edge.Second,
                edge.First));
            edgeIndex++;
        }

        return new TypedGraphSnapshot(nodes, relations);
    }
}
