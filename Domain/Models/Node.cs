using Abstractions;

public class Node {
    readonly List<Node> nodes = [];

    internal NodeState State;
    internal IReadOnlyCollection<Node> AttachedNodes => nodes;

    public virtual NodeLocalId LocalId => State.LocalId;
    public virtual InternalId GlobalId => State.GlobalId;

    public virtual ICollection<Edge> Edges => new EdgeCollection(State.Edges);
    public virtual ICollection<Incidence> Incidences { get; } = new List<Incidence>();
    public virtual ICollection<Node> Nodes => new NodeCollection(this);

    public virtual IDictionary<string, string> Attributes {
        get => State.Attributes;
        set => State.Attributes = value;
    }

    public Node(NodeLocalId localId) : this(new VirtualNodeState(localId)) { }

    internal Node(NodeState state) {
        State = state;
    }

    internal void Attach(Incidence incidence) {
        if (Incidences.Contains(incidence))
            return;
        Incidences.Add(incidence);
    }

    private sealed class NodeCollection(Node owner) : ICollection<Node> {
        public int Count => Snapshot().Count;
        public bool IsReadOnly => owner.State.Nodes.IsReadOnly;

        public void Add(Node item) {
            if (!owner.nodes.Any(node => SameNode(node, item)))
                owner.nodes.Add(item);
            owner.State.Nodes.Add(item.State);
        }

        public void Clear() {
            foreach (var node in Snapshot())
                owner.State.Nodes.Remove(node.State);
            owner.nodes.Clear();
        }

        public bool Contains(Node item) =>
            Snapshot().Any(node => SameNode(node, item));

        public void CopyTo(Node[] array, int arrayIndex) =>
            Snapshot().CopyTo(array, arrayIndex);

        public IEnumerator<Node> GetEnumerator() =>
            Snapshot().GetEnumerator();

        public bool Remove(Node item) {
            var removedFromBacking = owner.nodes.RemoveAll(node => SameNode(node, item)) > 0;
            var removedFromState = owner.State.Nodes.Remove(item.State);
            if (removedFromState)
                return true;

            var state = owner.State.Nodes.FirstOrDefault(state => state.GlobalId == item.GlobalId);
            return removedFromBacking || state is not null && owner.State.Nodes.Remove(state);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private List<Node> Snapshot() {
            var nodes = new List<Node>(owner.nodes);
            foreach (var state in owner.State.Nodes) {
                if (!nodes.Any(node => ReferenceEquals(node.State, state) || node.GlobalId == state.GlobalId))
                    nodes.Add(new Node(state));
            }
            return nodes;
        }

        private static bool SameNode(Node left, Node right) =>
            ReferenceEquals(left, right)
            || ReferenceEquals(left.State, right.State)
            || left.GlobalId == right.GlobalId;
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
