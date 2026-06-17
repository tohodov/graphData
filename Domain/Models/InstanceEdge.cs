namespace GraphData.Core.Models;

public class InstanceEdge : Edge, IGraphEdgeType
{
    internal InstanceEdge(EdgeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.EdgeInstance;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
