using Abstractions;

namespace GraphData.Core.Models;

public class Edge {
    private readonly EdgeState? _state;

    internal EdgeState State => _state ?? throw new InvalidOperationException(
        $"Edge '{GetType().Name}' is a type descriptor and is not bound to a graph edge.");

    protected Edge() {
    }

    internal Edge(EdgeState state) {
        _state = state;
    }

    public virtual Node Node1 => new(State.Node1);
    public virtual Node Node2 => new(State.Node2);

    public virtual NodeGlobalId? TypeId => State.TypeId;
}
