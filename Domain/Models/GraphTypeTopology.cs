using Abstractions;

namespace GraphData.Core.Models;

internal static class GraphTypeTopology
{
    public static IReadOnlyCollection<NodeState> NeighborStates(NodeState node) =>
        node.Edges
            .Select(edge => OtherEndpoint(node, edge))
            .Where(neighbor => neighbor.GlobalId != node.GlobalId)
            .GroupBy(static neighbor => neighbor.GlobalId)
            .Select(static group => group.First())
            .ToArray();

    public static bool IsNodeType(NodeState node) =>
        !IsNodeTypeRoot(node)
        && (node.GlobalId == GraphBaseTypeIds.NodeType
            || IsConnectedTo(node, GraphBaseTypeIds.NodeType));

    public static bool IsEdgeType(NodeState node) =>
        !IsEdgeTypeRoot(node)
        && (node.GlobalId == GraphBaseTypeIds.EdgeType
            || IsConnectedTo(node, GraphBaseTypeIds.EdgeType));

    public static bool IsGraphType(NodeState node) =>
        IsNodeType(node) || IsEdgeType(node);

    public static bool IsConnectedTo(NodeState node, InternalId targetId) =>
        NeighborStates(node).Any(neighbor => neighbor.GlobalId == targetId);

    private static NodeState OtherEndpoint(NodeState node, EdgeState edge) =>
        edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1;

    private static bool IsNodeTypeRoot(NodeState node) =>
        node.GlobalId == GraphSystemNodeIds.NodeTypeRoot
        || node.GlobalId == GraphSystemNodeIds.TypeRoot;

    private static bool IsEdgeTypeRoot(NodeState node) =>
        node.GlobalId == GraphSystemNodeIds.EdgeTypeRoot
        || node.GlobalId == GraphSystemNodeIds.TypeRoot;
}
