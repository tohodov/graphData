namespace Abstractions;

internal class EdgeState
{
    protected EdgeState() { } //TODO подумать над иерархией

    public EdgeState(NodeState first, NodeState second, NodeGlobalId? type = null) {
        Node1 = first;
        Node2 = second;
        TypeId = type;
    }

    public virtual NodeState Node1 { get; }
    public virtual NodeState Node2 { get; }
    public virtual NodeGlobalId? TypeId { get; }
}
