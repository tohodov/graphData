global using InternalId = Abstractions.NodeRef.InternalId;
global using NodePath = Abstractions.NodeRef.NodePath;
using Abstractions;

public class Edge {
    internal EdgeState State;

    public virtual Node Node1 => new(State.Node1);
    public virtual Node Node2 => new(State.Node2);

    internal Edge(EdgeState state) {
        State = state;
    }
    public Edge(Node node1, Node node2) : this(new EdgeStateReferenced(node1.State, node2.State)) { }

}
