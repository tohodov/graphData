using Abstractions;

namespace GraphData.Core.Models;

internal static class GraphRuntimeMetadata
{
    public const string NodeElement = "node";
    public const string EdgeElement = "edge";
    public const string TypeKind = "type";
    public const string EdgeInstanceKind = "edge-instance";
    public const string RelationRootKind = "relation-root";

    public static bool IsNodeType(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, TypeKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, NodeElement);

    public static bool IsEdgeType(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, TypeKind)
        && HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement);

    public static bool IsGraphType(NodeState node) =>
        HasAttribute(node, GraphRuntimeAttributeNames.GraphKind, TypeKind)
        && (HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, NodeElement)
            || HasAttribute(node, GraphRuntimeAttributeNames.GraphElement, EdgeElement));

    public static bool HasAttribute(NodeState node, string key, string value) =>
        node.Attributes.TryGetValue(key, out var actual)
        && string.Equals(actual, value, StringComparison.Ordinal);
}
