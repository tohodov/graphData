global using InternalId = Abstractions.NodeRef.InternalId;
global using NodePath = Abstractions.NodeRef.NodePath;
using Abstractions;

public class Edge {
    internal EdgeState State;

    public virtual Node Node1 => new(State.Node1);
    public virtual Node Node2 => new(State.Node2);

    public IList<Incidence> Incidences { get; } = new List<Incidence>();

    internal Edge(EdgeState state) {
        State = state;
    }
    public Edge(Node node1, Node node2) : this(new EdgeStateReferenced(node1.State, node2.State)) { }

    protected TIncidence Attach<TIncidence>(TIncidence incidence)
        where TIncidence : Incidence {
        Incidences.Add(incidence);
        incidence.Node.Attach(incidence);
        return incidence;
    }
}
public abstract class Incidence {
    public Node Node { get; }
    public Edge Edge { get; }

    protected Incidence(Node node, Edge edge) {
        Node = node;
        Edge = edge;
    }
}
public abstract class Incidence<TEdge> : Incidence
    where TEdge : Edge {
    public new TEdge Edge { get; }

    protected Incidence(Node node, TEdge edge)
        : base(node, edge) {
        Edge = edge;
    }
}
internal sealed class InstanceOf : Edge {
    public InstanceEnd Instance { get; }
    public TypeEnd Type { get; }

    public Node InstanceNode => Instance.Node;
    public Node TypeNode => Type.Node;

    public InstanceOf(EdgeState state, Node instance, Node type) : base(state) {
        Instance = Attach(new InstanceEnd(instance, this));
        Type = Attach(new TypeEnd(type, this));
    }

    public sealed class InstanceEnd : Incidence<InstanceOf> {
        public Node Type => Edge.Type.Node;
        public TypeEnd Opposite => Edge.Type;

        internal InstanceEnd(Node node, InstanceOf edge)
            : base(node, edge) {
        }
    }

    public sealed class TypeEnd : Incidence<InstanceOf> {
        public Node Instance => Edge.Instance.Node;
        public InstanceEnd Opposite => Edge.Instance;

        internal TypeEnd(Node node, InstanceOf edge)
            : base(node, edge) {
        }
    }
}