namespace GraphData.Core.Models;

public class TypeEdge : Edge, IGraphEdgeType
{
    public TypeEdge(EdgeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.EdgeType;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
