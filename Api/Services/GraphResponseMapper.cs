using GraphData.Api.Models;
using GraphData.Core.Models;

namespace GraphData.Api.Services;

public static class GraphResponseMapper
{
    public static SubgraphResponse ToSubgraphResponse(Subgraph subgraph)
    {
        var nodeNames = subgraph.Nodes
            .Select(static node => node.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = subgraph.Nodes
            .SelectMany(node => node.Nodes.Select(connected => ToEdgeResponse(node, connected)))
            .Where(edge => nodeNames.Contains(edge.SourceName) && nodeNames.Contains(edge.TargetName))
            .DistinctBy(static edge => EdgeKey(edge.SourceName, edge.TargetName))
            .OrderBy(static edge => edge.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SubgraphResponse
        {
            Nodes = subgraph.Nodes
                .OrderBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static node => ToNodeResponse(node))
                .ToArray(),
            Edges = edges
        };
    }

    public static NodeResponse ToNodeResponse(
        Node node,
        IEnumerable<EdgeResponse>? edges = null)
    {
        return new NodeResponse
        {
            Name = node.Name,
            Attributes = new Dictionary<string, string>(node.Attributes),
            Edges = edges?.ToArray() ?? Array.Empty<EdgeResponse>()
        };
    }

    public static NodeSearchMatchResponse ToSearchMatchResponse(NodeSearchMatch match)
    {
        return new NodeSearchMatchResponse
        {
            Node = ToNodeResponse(match.Node),
            Bindings = match.Bindings.ToDictionary(
                static binding => binding.Key,
                static binding => ToNodeResponse(binding.Value),
                StringComparer.OrdinalIgnoreCase),
            Score = match.Score,
            MatchedBy = match.MatchedBy
        };
    }

    public static EdgeResponse ToEdgeResponse(Node source, Node target)
    {
        return ToEdgeResponse(source.Name, target.Name);
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
