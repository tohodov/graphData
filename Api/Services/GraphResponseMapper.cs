using GraphData.Api.Models;
using GraphData.Core.Models;

namespace GraphData.Api.Services;

public static class GraphResponseMapper
{
    public static SubgraphResponse ToSubgraphResponse(Subgraph subgraph)
    {
        var nodes = subgraph.Nodes.ToArray();
        var nodeIds = nodes
            .Select(static node => node.GlobalId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = nodes
            .SelectMany(static node => node.Edges)
            .Where(edge => nodeIds.Contains(edge.Node1.GlobalId.ToString()) && nodeIds.Contains(edge.Node2.GlobalId.ToString()))
            .GroupBy(static edge => EdgeKey(edge.Node1.GlobalId.ToString(), edge.Node2.GlobalId.ToString()), StringComparer.OrdinalIgnoreCase)
            .Select(static group => ToEdgeResponse(group.First()))
            .ToArray();

        return new SubgraphResponse
        {
            Nodes = nodes.Select(static node => ToNodeResponse(node)).ToArray(),
            Edges = edges
        };
    }

    public static NodeResponse ToNodeResponse(
        Node node,
        IEnumerable<EdgeResponse>? edges = null)
    {
        return new NodeResponse
        {
            LocalId = node.LocalId.ToString(),
            GlobalId = node.GlobalId.ToString(),
            Attributes = new Dictionary<string, string>(node.Attributes),
            Edges = edges?.ToArray() ?? node.Edges.Select(edge => ToNodeEdgeResponse(node, edge)).ToArray()
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
        return string.Compare(source.GlobalId.ToString(), target.GlobalId.ToString(), StringComparison.OrdinalIgnoreCase) <= 0
            ? ToOrderedEdgeResponse(source, target)
            : ToOrderedEdgeResponse(target, source);
    }

    private static EdgeResponse ToEdgeResponse(Edge edge)
    {
        return ToEdgeResponse(edge.Node1, edge.Node2);
    }

    private static EdgeResponse ToNodeEdgeResponse(Node node, Edge edge)
    {
        var neighbor = edge.Node1.GlobalId == node.GlobalId
            ? edge.Node2
            : edge.Node1;

        return ToEdgeResponse(edge) with
        {
            NeighborLocalId = neighbor.LocalId.ToString()
        };
    }

    private static EdgeResponse ToOrderedEdgeResponse(Node source, Node target)
    {
        return new EdgeResponse
        {
            SourceLocalId = source.LocalId.ToString(),
            SourceGlobalId = source.GlobalId.ToString(),
            TargetLocalId = target.LocalId.ToString(),
            TargetGlobalId = target.GlobalId.ToString()
        };
    }

    private static string EdgeKey(string sourceGlobalId, string targetGlobalId)
    {
        return string.Compare(sourceGlobalId, targetGlobalId, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{sourceGlobalId}\0{targetGlobalId}"
            : $"{targetGlobalId}\0{sourceGlobalId}";
    }
}
