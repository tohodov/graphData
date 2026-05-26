using System;
using System.Collections.Generic;
using System.Linq;
using GraphData.Core.Models;

namespace GraphData.Tests.Performance;

internal sealed record GeneratedGraph(
    int NodeCount,
    int ConnectionsPerNode,
    int Seed,
    bool ContainsCycle,
    IReadOnlyList<GeneratedGraphNode> Nodes,
    IReadOnlyList<GeneratedGraphEdge> Edges)
{
    public static GeneratedGraph Create(int nodeCount, int connectionsPerNode, int seed)
    {
        if (nodeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeCount), "Node count must be positive.");
        }

        if (connectionsPerNode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionsPerNode), "Connections per node cannot be negative.");
        }

        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => new GeneratedGraphNode(
                index,
                new([$"node-{index:D6}"]),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "generated",
                    ["group"] = (index % 16).ToString("D2"),
                    ["payload"] = $"seed-{seed}-payload-{index:D6}"
                }))
            .ToArray();

        var edges = GenerateEdges(nodeCount, connectionsPerNode, seed);
        var containsCycle = ContainsUndirectedCycle(nodeCount, edges);
        if (nodeCount >= 3 && connectionsPerNode > 0 && !containsCycle)
        {
            throw new InvalidOperationException("Generated performance graph must contain at least one cycle.");
        }

        return new GeneratedGraph(nodeCount, connectionsPerNode, seed, containsCycle, nodes, edges);
    }

    public IReadOnlyList<NodeGlobalId> GetSampleNodeNames(int count, int seedOffset)
    {
        if (count <= 0)
            return Array.Empty<NodeGlobalId>();
        var random = new Random(Seed + seedOffset);
        return Enumerable.Range(0, count)
            .Select(_ => Nodes[random.Next(Nodes.Count)].Path)
            .ToArray();
    }

    private static GeneratedGraphEdge[] GenerateEdges(int nodeCount, int connectionsPerNode, int seed)
    {
        if (nodeCount < 2 || connectionsPerNode == 0)
            return Array.Empty<GeneratedGraphEdge>();

        var maxEdgeCount = nodeCount * (nodeCount - 1) / 2;
        var desiredEdgeCount = Math.Min(maxEdgeCount, nodeCount * connectionsPerNode);
        var edges = new HashSet<GeneratedGraphEdge>();

        for (var index = 0; index < nodeCount && edges.Count < desiredEdgeCount; index++)
        {
            edges.Add(GeneratedGraphEdge.Create(index, (index + 1) % nodeCount));
        }

        var random = new Random(seed);
        var attempts = 0;
        var maxAttempts = Math.Max(desiredEdgeCount * 50, nodeCount * 10);
        while (edges.Count < desiredEdgeCount && attempts < maxAttempts)
        {
            attempts++;
            var source = random.Next(nodeCount);
            var target = (source + 1 + random.Next(nodeCount - 1)) % nodeCount;
            edges.Add(GeneratedGraphEdge.Create(source, target));
        }

        for (var source = 0; source < nodeCount && edges.Count < desiredEdgeCount; source++)
        {
            for (var target = source + 1; target < nodeCount && edges.Count < desiredEdgeCount; target++)
            {
                edges.Add(new GeneratedGraphEdge(source, target));
            }
        }

        return edges
            .OrderBy(static edge => edge.SourceIndex)
            .ThenBy(static edge => edge.TargetIndex)
            .ToArray();
    }

    private static bool ContainsUndirectedCycle(int nodeCount, IEnumerable<GeneratedGraphEdge> edges)
    {
        var parents = Enumerable.Range(0, nodeCount).ToArray();
        var ranks = new int[nodeCount];

        foreach (var edge in edges)
        {
            if (!Union(edge.SourceIndex, edge.TargetIndex))
            {
                return true;
            }
        }

        return false;

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        bool Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot == secondRoot)
            {
                return false;
            }

            if (ranks[firstRoot] < ranks[secondRoot])
            {
                parents[firstRoot] = secondRoot;
            }
            else if (ranks[firstRoot] > ranks[secondRoot])
            {
                parents[secondRoot] = firstRoot;
            }
            else
            {
                parents[secondRoot] = firstRoot;
                ranks[firstRoot]++;
            }

            return true;
        }
    }
}

internal sealed record GeneratedGraphNode(
    int Index,
    NodeGlobalId Path,
    Dictionary<string, string> Attributes);

internal sealed record GeneratedGraphEdge(int SourceIndex, int TargetIndex)
{
    public static GeneratedGraphEdge Create(int firstIndex, int secondIndex)
    {
        if (firstIndex == secondIndex)
        {
            throw new ArgumentException("Self edges are not generated for performance fixtures.");
        }

        return firstIndex < secondIndex
            ? new GeneratedGraphEdge(firstIndex, secondIndex)
            : new GeneratedGraphEdge(secondIndex, firstIndex);
    }
}
