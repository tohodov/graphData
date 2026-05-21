using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphData.Tests.Performance;

internal sealed record GeneratedGraph(
    int NodeCount,
    int ConnectionsPerNode,
    int Seed,
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
                $"node-{index:D6}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "generated",
                    ["group"] = (index % 16).ToString("D2"),
                    ["payload"] = $"seed-{seed}-payload-{index:D6}"
                }))
            .ToArray();

        var edges = GenerateEdges(nodeCount, connectionsPerNode, seed);
        return new GeneratedGraph(nodeCount, connectionsPerNode, seed, nodes, edges);
    }

    public IReadOnlyList<string> GetSampleNodeNames(int count, int seedOffset)
    {
        if (count <= 0)
        {
            return Array.Empty<string>();
        }

        var random = new Random(Seed + seedOffset);
        return Enumerable.Range(0, count)
            .Select(_ => Nodes[random.Next(Nodes.Count)].Name)
            .ToArray();
    }

    private static GeneratedGraphEdge[] GenerateEdges(int nodeCount, int connectionsPerNode, int seed)
    {
        if (nodeCount < 2 || connectionsPerNode == 0)
        {
            return Array.Empty<GeneratedGraphEdge>();
        }

        var maxEdgeCount = nodeCount * (nodeCount - 1) / 2;
        var desiredEdgeCount = Math.Min(maxEdgeCount, nodeCount * connectionsPerNode);
        var edges = new HashSet<GeneratedGraphEdge>();

        for (var index = 0; index < nodeCount - 1 && edges.Count < desiredEdgeCount; index++)
        {
            edges.Add(GeneratedGraphEdge.Create(index, index + 1));
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
}

internal sealed record GeneratedGraphNode(
    int Index,
    string Name,
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
