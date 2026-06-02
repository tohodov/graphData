namespace GraphData.Core.Models;

public class TypeNode : Node, IGraphNodeType
{
    public TypeNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeType;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
