using Abstractions;

namespace GraphData.Core.Models;

internal sealed class TypeEdge : EdgeType, IGraphEdgeType
{
    internal TypeEdge(EdgeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.EdgeType;

    public override NodeGlobalId? TypeId => StaticTypeId;
}
