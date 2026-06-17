namespace GraphData.Core.Models;

public class Node {
    public Node(NodeState state) {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    protected NodeState? State { get; }

    public virtual NodeLocalId LocalId => RequireState().LocalId;
    public virtual NodeGlobalId GlobalId => RequireState().GlobalId;
     
    public virtual ICollection<Edge> Edges => RequireState().Edges;
    public virtual ICollection<Node> Nodes => RequireState().Nodes;

    public virtual IDictionary<string, string> Attributes {
        get => RequireState().Attributes;
        set => RequireState().Attributes = value;
    }

    public virtual NodeGlobalId? TypeId => State?.TypeId;

    public bool TryGetState<TState>(out TState state) where TState : NodeState {
        if (State is TState typed) {
            state = typed;
            return true;
        }

        state = null!;
        return false;
    }

    private NodeState RequireState() {
        return State ?? throw new InvalidOperationException(
            $"Node type '{GetType().Name}' must either pass a NodeState to the base constructor or override the requested member.");
    }
}
