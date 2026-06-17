namespace GraphData.Core.Models;

public class InstanceNode : Node, IGraphNodeType
{
    public InstanceNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeInstance;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
