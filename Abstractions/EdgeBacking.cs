namespace Abstractions;

internal abstract class EdgeBacking {
    public abstract NodeBacking Node1 { get; }
    public abstract NodeBacking Node2 { get; }

    protected EdgeBacking() { }
}
internal sealed class InMemoryEdgeBacking : EdgeBacking {
    public override NodeBacking Node1 { get; }
    public override NodeBacking Node2 { get; }

    public InMemoryEdgeBacking(NodeBacking first, NodeBacking second) {
        Node1 = first;
        Node2 = second;
    }
}
