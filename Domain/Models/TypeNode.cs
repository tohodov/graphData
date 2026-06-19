using Abstractions;

namespace GraphData.Core.Models;

public class TypeNode : Node, IGraphNodeType
{
    internal TypeNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeType;
}
