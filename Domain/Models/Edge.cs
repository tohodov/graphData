using Abstractions;

namespace GraphData.Core.Models;

public class Edge {
    internal readonly EdgeState State;

    internal Edge(EdgeState state) {
        State = state;
    }

    public virtual Node Node1 => new(State.Node1);
    public virtual Node Node2 => new(State.Node2);

    public virtual NodeGlobalId? TypeId => State.TypeId;
}
