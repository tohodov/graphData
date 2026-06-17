namespace GraphData.Core.Models;

public class Node {
    public Node(NodeState state) {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    protected NodeState? State { get; }

    public virtual NodeLocalId LocalId => RequireState().LocalId;
    public virtual NodeGlobalId GlobalId => RequireState().GlobalId;
     
    public virtual ICollection<Edge> Edges => new EdgeCollection(RequireState().Edges);
    public virtual ICollection<Node> Nodes => new NodeCollection(RequireState().Nodes);

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

    internal NodeState RequireState() {
        return State ?? throw new InvalidOperationException(
            $"Node type '{GetType().Name}' must either pass a NodeState to the base constructor or override the requested member.");
    }

    private sealed class NodeCollection(ICollection<NodeState> states) : ICollection<Node> {
        public int Count => states.Count;
        public bool IsReadOnly => states.IsReadOnly;

        public void Add(Node item) => states.Add(item.RequireState());

        public void Clear() => states.Clear();

        public bool Contains(Node item) =>
            states.Any(state => state.GlobalId == item.GlobalId);

        public void CopyTo(Node[] array, int arrayIndex) =>
            states.Select(static state => new Node(state)).ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<Node> GetEnumerator() =>
            states.Select(static state => new Node(state)).GetEnumerator();

        public bool Remove(Node item) {
            if (states.Remove(item.RequireState()))
                return true;

            var state = states.FirstOrDefault(state => state.GlobalId == item.GlobalId);
            return state is not null && states.Remove(state);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EdgeCollection(ICollection<EdgeState> states) : ICollection<Edge> {
        public int Count => states.Count;
        public bool IsReadOnly => states.IsReadOnly;

        public void Add(Edge item) => states.Add(item.RequireState());

        public void Clear() => states.Clear();

        public bool Contains(Edge item) {
            var state = item.RequireState();
            return states.Any(candidate =>
                candidate.Node1.GlobalId == state.Node1.GlobalId
                && candidate.Node2.GlobalId == state.Node2.GlobalId);
        }

        public void CopyTo(Edge[] array, int arrayIndex) =>
            states.Select(static state => new Edge(state)).ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<Edge> GetEnumerator() =>
            states.Select(static state => new Edge(state)).GetEnumerator();

        public bool Remove(Edge item) {
            var state = item.RequireState();
            if (states.Remove(state))
                return true;

            var match = states.FirstOrDefault(candidate =>
                candidate.Node1.GlobalId == state.Node1.GlobalId
                && candidate.Node2.GlobalId == state.Node2.GlobalId);
            return match is not null && states.Remove(match);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
