using Abstractions;

public class Node {
    internal NodeState State;

    public virtual NodeLocalId LocalId => State.LocalId;
    public virtual InternalId GlobalId => State.GlobalId;

    public virtual ICollection<Edge> Edges => new EdgeCollection(State.Edges);
    public virtual ICollection<Node> Nodes => new NodeCollection(State.Nodes);

    public virtual IDictionary<string, string> Attributes {
        get => State.Attributes;
        set => State.Attributes = value;
    }

    public Node(NodeLocalId localId) : this(new VirtualNodeState(localId)) { }

    public Node(InternalId globalId) : this(new VirtualNodeState(globalId)) { }

    internal Node(NodeState state) {
        State = state;
    }

    private sealed class NodeCollection(ICollection<NodeState> states) : ICollection<Node> {
        public int Count => states.Count;
        public bool IsReadOnly => states.IsReadOnly;

        public void Add(Node item) => states.Add(item.State);

        public void Clear() => states.Clear();

        public bool Contains(Node item) =>
            states.Any(state => state.GlobalId == item.GlobalId);

        public void CopyTo(Node[] array, int arrayIndex) =>
            states.Select(static state => new Node(state)).ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<Node> GetEnumerator() =>
            states.Select(static state => new Node(state)).GetEnumerator();

        public bool Remove(Node item) {
            if (states.Remove(item.State))
                return true;

            var state = states.FirstOrDefault(state => state.GlobalId == item.GlobalId);
            return state is not null && states.Remove(state);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EdgeCollection(ICollection<EdgeState> states) : ICollection<Edge> {
        public int Count => states.Count;
        public bool IsReadOnly => states.IsReadOnly;

        public void Add(Edge item) => states.Add(item.State);

        public void Clear() => states.Clear();

        public bool Contains(Edge item) {
            var state = item.State;
            return states.Any(candidate =>
                candidate.Node1.GlobalId == state.Node1.GlobalId
                && candidate.Node2.GlobalId == state.Node2.GlobalId);
        }

        public void CopyTo(Edge[] array, int arrayIndex) =>
            states.Select(static state => new Edge(state)).ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<Edge> GetEnumerator() =>
            states.Select(static state => new Edge(state)).GetEnumerator();

        public bool Remove(Edge item) {
            var state = item.State;
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
