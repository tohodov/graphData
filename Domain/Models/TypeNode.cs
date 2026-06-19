using Abstractions;

namespace GraphData.Core.Models;

public class TypeNode : Node, IGraphNodeType
{
    internal TypeNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeType;

    public NodeTypeDefinition Define(Action<NodeTypeBuilder>? configure = null) {
        var builder = new NodeTypeBuilder(this);
        configure?.Invoke(builder);
        return builder.Build();
    }

    internal static bool IsTypeNodeId(NodeGlobalId id) => IsChildOf(id, GraphSystemNodeIds.NodeTypeRoot);

    private static bool IsChildOf(NodeGlobalId id, NodeGlobalId root) {
        var idSegments = id.ToArray();
        var rootSegments = root.ToArray();
        if (idSegments.Length <= rootSegments.Length)
            return false;

        for (var index = 0; index < rootSegments.Length; index++)
            if (idSegments[index] != rootSegments[index])
                return false;

        return true;
    }
}
