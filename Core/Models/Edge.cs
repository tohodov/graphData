namespace GraphData.Core.Models;

public abstract class Edge
{
    protected Edge() {
    }

    protected Edge(EdgeState state) {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    protected EdgeState? State { get; }

    public virtual Node Node1 => RequireState().Node1;
    public virtual Node Node2 => RequireState().Node2;

    public virtual NodeGlobalId? TypeId => State?.TypeId;

    public bool TryGetState<TState>(out TState state) where TState : EdgeState {
        if (State is TState typed) {
            state = typed;
            return true;
        }

        state = null!;
        return false;
    }

    private EdgeState RequireState() {
        return State ?? throw new InvalidOperationException(
            $"Edge type '{GetType().Name}' must either pass an EdgeState to the base constructor or override the requested member.");
    }
}
