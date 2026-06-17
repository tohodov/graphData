namespace GraphData.Core.Models;

public class Edge {
    protected EdgeState State;

    public Edge(EdgeState state) {
        State = state;
    }

    public virtual Node Node1 => State.Node1;
    public virtual Node Node2 => State.Node2;

    public virtual NodeGlobalId? TypeId => State.TypeId;
}
