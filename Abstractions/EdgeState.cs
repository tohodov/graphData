namespace Abstractions;

internal abstract class EdgeState {
    public abstract NodeState Node1 { get; }
    public abstract NodeState Node2 { get; }

    protected EdgeState() { }
}
internal class EdgeStateReferenced : EdgeState {
    public override NodeState Node1 { get; }
    public override NodeState Node2 { get; }

    public EdgeStateReferenced(NodeState first, NodeState second) {
        Node1 = first;
        Node2 = second;
    }
}