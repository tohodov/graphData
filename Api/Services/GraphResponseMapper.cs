using GraphData.Api.Models;
using GraphData.Core.Models;

namespace GraphData.Api.Services;

public static class GraphResponseMapper
{
    public static SubgraphResponse ToSubgraphResponse(Subgraph subgraph)
    {
        throw new NotImplementedException();
    }

    public static NodeResponse ToNodeResponse(
        Node node,
        IEnumerable<EdgeResponse>? edges = null)
    {
        return new NodeResponse
        {
            Name = node.LocalId,
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
        return ToEdgeResponse(source.LocalId, target.LocalId);
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
