using Abstractions;

namespace GraphData.Core.Models;

internal sealed class TypeNode : NodeType, IGraphNodeType
{
    internal TypeNode(NodeState state)
        : base(state) {
    }

    public static InternalId StaticTypeId => GraphBaseTypeIds.NodeType;
}
