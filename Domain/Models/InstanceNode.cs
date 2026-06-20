using Abstractions;

namespace GraphData.Core.Models;

public class InstanceNode : Node, IGraphNodeType
{
    internal InstanceNode(NodeState state)
        : base(state) {
    }

    public static InternalId StaticTypeId => GraphBaseTypeIds.NodeInstance;

    public virtual InternalId TypeId => StaticTypeId;

    public IReadOnlyCollection<NodeType> AssignedTypes => field ??= GraphTypeTopology.NeighborStates(State)
        .Where(GraphTypeTopology.IsNodeType)
        .GroupBy(static node => node.GlobalId)
        .Select(static group => NodeType.FromState(group.First()))
        .ToArray();

    public NodeType? SingleAssignedType => AssignedTypes.Count == 1
        ? AssignedTypes.Single()
        : null;

    public IReadOnlyCollection<InstanceNode> NeighborInstances => field ??= GraphTypeTopology.NeighborStates(State)
        .Where(static node => !GraphTypeTopology.IsGraphType(node))
        .GroupBy(static node => node.GlobalId)
        .Select(static group => new InstanceNode(group.First()))
        .ToArray();
}
