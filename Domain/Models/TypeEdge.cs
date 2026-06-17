using Abstractions;

namespace GraphData.Core.Models;

public class TypeEdge : Edge, IGraphEdgeType
{
    internal TypeEdge(EdgeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.EdgeType;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
