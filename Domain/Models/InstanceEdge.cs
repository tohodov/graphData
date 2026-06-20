using Abstractions;

namespace GraphData.Core.Models;

public class InstanceEdge : Edge, IGraphEdgeType
{
    internal InstanceEdge(EdgeState state)
        : base(state) {
    }

    public static InternalId StaticTypeId => GraphBaseTypeIds.EdgeInstance;

    public override InternalId? TypeId => StaticTypeId;
}
