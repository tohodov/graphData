using GraphData.Api.Models;
using GraphData.Core.Models;

namespace GraphData.Api.Services;

public static class GraphResponseMapper
{
    public static SubgraphResponse ToSubgraphResponse(Subgraph subgraph)
    {
        var nodes = subgraph.Nodes.ToArray();
        var nodeNames = nodes
            .Select(static node => node.GlobalId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = nodes
            .SelectMany(static node => node.Edges)
            .Where(edge => nodeNames.Contains(edge.Node1.GlobalId.ToString()) && nodeNames.Contains(edge.Node2.GlobalId.ToString()))
            .GroupBy(static edge => EdgeKey(edge.Node1.GlobalId.ToString(), edge.Node2.GlobalId.ToString()), StringComparer.OrdinalIgnoreCase)
            .Select(static group => ToEdgeResponse(group.First()))
            .ToArray();

        return new SubgraphResponse
        {
            Nodes = nodes.Select(static node => ToNodeResponse(node, [])).ToArray(),
            Edges = edges
        };
    }

    public static NodeResponse ToNodeResponse(
        Node node,
        IEnumerable<EdgeResponse>? edges = null)
    {
        return new NodeResponse
        {
            Name = node.GlobalId.ToString(),
            Attributes = new Dictionary<string, string>(node.Attributes),
            Edges = edges?.ToArray() ?? node.Edges.Select(ToEdgeResponse).ToArray()
        };
    }

    public static NodeSearchMatchResponse ToSearchMatchResponse(NodeSearchMatch match)
    {
        return new NodeSearchMatchResponse
        {
            Node = ToNodeResponse(match.Node),
            Bindings = match.Bindings.ToDictionary(
                static binding => binding.Key,
                static binding => ToNodeResponse(binding.Value, []),
                StringComparer.OrdinalIgnoreCase),
            Score = match.Score,
            MatchedBy = match.MatchedBy
        };
    }

    public static EdgeResponse ToEdgeResponse(Node source, Node target)
    {
        return ToEdgeResponse(source.GlobalId.ToString(), target.GlobalId.ToString());
    }

    private static EdgeResponse ToEdgeResponse(Edge edge)
    {
        return ToEdgeResponse(edge.Node1, edge.Node2);
    }

    private static EdgeResponse ToEdgeResponse(string sourceName, string targetName)
    {
        return string.Compare(sourceName, targetName, StringComparison.OrdinalIgnoreCase) <= 0
            ? new EdgeResponse { SourceName = sourceName, TargetName = targetName }
            : new EdgeResponse { SourceName = targetName, TargetName = sourceName };
    }

    private static string EdgeKey(string sourceName, string targetName)
    {
        return string.Compare(sourceName, targetName, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{sourceName}\0{targetName}"
            : $"{targetName}\0{sourceName}";
    }
}
