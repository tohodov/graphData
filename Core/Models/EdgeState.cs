namespace GraphData.Core.Models;

public class EdgeState
{
    protected EdgeState() { } //TODO подумать об иерархии
    public EdgeState(Node first, Node second, NodeGlobalId? type = null) {
        Node1 = first;
        Node2 = second;
        TypeId = type;
    }
    public virtual Node Node1 { get; }
    public virtual Node Node2 { get; }
    public virtual NodeGlobalId? TypeId { get; }
}
