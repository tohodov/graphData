global using InternalId = Abstractions.NodeRef.InternalId;
global using NodePath = Abstractions.NodeRef.NodePath;
using Abstractions;

public class CarrierEdge {
    internal CarrierEdgeBacking Backing;

    public virtual CarrierNode Node1 => new(Backing.Node1);
    public virtual CarrierNode Node2 => new(Backing.Node2);

    public IList<Incidence> Incidences { get; } = new List<Incidence>();

    internal CarrierEdge(CarrierEdgeBacking state) {
        Backing = state;
    }
    public CarrierEdge(CarrierNode node1, CarrierNode node2) : this(new InMemoryEdgeBacking(node1.Backing, node2.Backing)) { }

    protected TIncidence Attach<TIncidence>(TIncidence incidence)
        where TIncidence : Incidence {
        if (Incidences.Contains(incidence))
            return incidence;
        Incidences.Add(incidence);
        incidence.Node.Attach(incidence);
        return incidence;
    }
}
public abstract class Incidence {
    public CarrierNode Node { get; }
    public CarrierEdge Edge { get; }

    protected Incidence(CarrierNode node, CarrierEdge edge) {
        Node = node;
        Edge = edge;
    }
}
public abstract class Incidence<TEdge> : Incidence
    where TEdge : CarrierEdge {
    public new TEdge Edge { get; }

    protected Incidence(CarrierNode node, TEdge edge)
        : base(node, edge) {
        Edge = edge;
    }
}
internal sealed class InstanceOf : CarrierEdge {
    public InstanceEnd Instance { get; }
    public TypeEnd Type { get; }
    public CarrierNode? Witness { get; }

    public CarrierNode InstanceNode => Instance.Node;
    public CarrierNode TypeNode => Type.Node;

    public InstanceOf(CarrierEdgeBacking state, CarrierNode instance, CarrierNode type, CarrierNode? witness = null) : base(state) {
        Witness = witness;
        Instance = Attach(new InstanceEnd(instance, this));
        Type = Attach(new TypeEnd(type, this));
    }

    public sealed class InstanceEnd : Incidence<InstanceOf> {
        public CarrierNode Type => Edge.Type.Node;
        public TypeEnd Opposite => Edge.Type;

        internal InstanceEnd(CarrierNode node, InstanceOf edge)
            : base(node, edge) {
        }
    }

    public sealed class TypeEnd : Incidence<InstanceOf> {
        public CarrierNode Instance => Edge.Instance.Node;
        public InstanceEnd Opposite => Edge.Instance;

        internal TypeEnd(CarrierNode node, InstanceOf edge)
            : base(node, edge) {
        }
    }
}
