using Abstractions;

namespace GraphData.Core.Models;

public class InstanceNode : Node, IGraphNodeType
{
    internal InstanceNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeInstance;

    public virtual NodeGlobalId TypeId => StaticTypeId;

    public Edge EdgeToType => field ??= new Edge(State.Edges.First(x => x.Node2.GlobalId == GraphBaseTypeIds.NodeType));
    public TypeNode Type => field ??= new TypeNode(EdgeToType.Node2.State);
}
