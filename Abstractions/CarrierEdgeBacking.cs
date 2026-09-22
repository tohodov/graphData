namespace Abstractions;

internal abstract class CarrierEdgeBacking {
    public abstract CarrierNodeBacking Node1 { get; }
    public abstract CarrierNodeBacking Node2 { get; }

    protected CarrierEdgeBacking() { }
}
internal sealed class InMemoryEdgeBacking : CarrierEdgeBacking {
    public override CarrierNodeBacking Node1 { get; }
    public override CarrierNodeBacking Node2 { get; }

    public InMemoryEdgeBacking(CarrierNodeBacking first, CarrierNodeBacking second) {
        Node1 = first;
        Node2 = second;
    }
}
