namespace Abstractions;

internal abstract class EdgeBacking {
    public abstract NodeBacking Node1 { get; }
    public abstract NodeBacking Node2 { get; }

    protected EdgeBacking() { }
}
internal class EdgeStateReferenced : EdgeBacking {//TODO кажется такое не должно существовать, надо удалить
    public override NodeBacking Node1 { get; }
    public override NodeBacking Node2 { get; }

    public EdgeStateReferenced(NodeBacking first, NodeBacking second) {
        Node1 = first;
        Node2 = second;
    }
}