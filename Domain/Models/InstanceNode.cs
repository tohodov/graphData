using Abstractions;

namespace GraphData.Core.Models;

public class InstanceNode : Node, IGraphNodeType
{
    internal InstanceNode(NodeState state)
        : base(state) {
    }

    public static NodeGlobalId StaticTypeId => GraphBaseTypeIds.NodeInstance;

    public virtual NodeGlobalId TypeId => StaticTypeId;

    public IReadOnlyCollection<NodeType> AssignedTypes => field ??= State.Edges
        .Select(OtherEndpoint)
        .Where(static node => NodeType.IsNodeTypeId(node.GlobalId))
        .GroupBy(static node => node.GlobalId)
        .Select(static group => NodeType.FromState(group.First()))
        .ToArray();

    public NodeType? SingleAssignedType => AssignedTypes.Count == 1
        ? AssignedTypes.Single()
        : null;

    public IReadOnlyCollection<InstanceNode> NeighborInstances => field ??= State.Edges
        .Select(OtherEndpoint)
        .Where(static node => !NodeType.IsNodeTypeId(node.GlobalId))
        .GroupBy(static node => node.GlobalId)
        .Select(static group => new InstanceNode(group.First()))
        .ToArray();

    private NodeState OtherEndpoint(EdgeState edge) =>
        edge.Node1.GlobalId == GlobalId ? edge.Node2 : edge.Node1;
}
