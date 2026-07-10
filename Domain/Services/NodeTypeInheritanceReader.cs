using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal static class NodeTypeInheritanceReader
{
    private static readonly NodeLocalId RequiresTypeLocalId =
        NodeType.CreateDefaultLocalId(typeof(RequiresConnectionNodeType));

    public static IReadOnlyCollection<NodeType> ReadRequiredTypes(NodeType derivedType)
    {
        var nodeTypesRoot = derivedType.Nodes.FirstOrDefault(node =>
            TypedEdgeSubgraphCodec.IsDirectChildOf(derivedType.GlobalId, node.GlobalId));
        if (nodeTypesRoot is null)
            return Array.Empty<NodeType>();

        var requiresType = nodeTypesRoot.Nodes
            .Where(node => node.LocalId == RequiresTypeLocalId)
            .SingleOrDefault();
        if (requiresType is null)
            return Array.Empty<NodeType>();

        var requiresNodeType = new NodeType(requiresType.Backing);
        var derivedMemberTypeId = TypedEdgeSubgraphCodec.MemberTypeId(
            requiresNodeType,
            nameof(RequiresConnectionNodeType.Derived));
        var requiredMemberTypeId = TypedEdgeSubgraphCodec.MemberTypeId(
            requiresNodeType,
            nameof(RequiresConnectionNodeType.Required));
        var result = new Dictionary<InternalId, NodeType>();
        foreach (var derivedEndpoint in derivedType.Nodes
                     .Where(node => TypedEdgeSubgraphCodec.HasMemberClassifier(node, derivedMemberTypeId))) {
            var relations = derivedEndpoint.Nodes
                .Where(node => TypedEdgeSubgraphCodec.IsDirectChildOf(derivedEndpoint.GlobalId, node.GlobalId))
                .Where(node => node.Nodes.Any(classifier => classifier.GlobalId == requiresType.GlobalId))
                .ToArray();
            if (relations.Length != 1)
                throw new InvalidOperationException(
                    $"Requires endpoint '{derivedEndpoint.GlobalId}' must belong to exactly one requires relation, but found {relations.Length}.");

            var relation = relations[0];
            var requiredEndpoints = relation.Nodes
                .Where(node => TypedEdgeSubgraphCodec.IsDirectChildOf(node.GlobalId, relation.GlobalId))
                .Where(node => TypedEdgeSubgraphCodec.HasMemberClassifier(node, requiredMemberTypeId))
                .ToArray();
            if (requiredEndpoints.Length != 1)
                throw new InvalidOperationException(
                    $"Requires relation '{relation.GlobalId}' must contain exactly one required endpoint, but found {requiredEndpoints.Length}.");

            var requiredTypes = requiredEndpoints[0].Nodes
                .Where(node => node.GlobalId != relation.GlobalId)
                .Where(node => IsNodeType(node, nodeTypesRoot))
                .ToArray();
            if (requiredTypes.Length != 1)
                throw new InvalidOperationException(
                    $"Requires relation '{relation.GlobalId}' must reference exactly one required type, but found {requiredTypes.Length}.");

            var requiredType = requiredTypes[0];
            result[requiredType.GlobalId] = new NodeType(requiredType.Backing);
        }

        return result.Values.ToArray();
    }

    private static bool IsNodeType(Node node, Node nodeTypesRoot)
    {
        return node.GlobalId != nodeTypesRoot.GlobalId
            && node.Nodes.Any(neighbor => neighbor.GlobalId == nodeTypesRoot.GlobalId);
    }
}
