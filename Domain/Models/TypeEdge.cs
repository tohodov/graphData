using Abstractions;

namespace GraphData.Core.Models;

internal sealed class TypeEdge : EdgeType, IGraphEdgeType
{
    internal TypeEdge(EdgeState state)
        : base(state) {
    }

    public static InternalId StaticTypeId => GraphBaseTypeIds.EdgeType;

    public override InternalId? TypeId => StaticTypeId;
}
