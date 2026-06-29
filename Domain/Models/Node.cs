using Abstractions;

public class Node {
    readonly List<Node> attachedNodes = [];
    bool materializingAttachedNodes;

    internal NodeBacking Backing { get; private set; }
    internal IReadOnlyCollection<Node> AttachedNodes => attachedNodes;

    public virtual NodeLocalId LocalId => Backing.LocalId;
    public virtual InternalId GlobalId => Backing.GlobalId;

    public virtual ICollection<Edge> Edges => new EdgeCollection(Backing.Edges);
    public virtual ICollection<Incidence> Incidences { get; } = new List<Incidence>();
    public virtual ICollection<Node> Nodes => new NodeCollection(this);

    public virtual IDictionary<string, string> Attributes {
        get => Backing.Attributes;
        set => Backing.Attributes = value;
    }

    public Node(NodeLocalId localId) : this(new VirtualNodeState(localId)) { }

    internal Node(NodeBacking state) {
        Backing = state;
    }

    internal void Attach(Incidence incidence) {
        if (Incidences.Contains(incidence))
            return;
        Incidences.Add(incidence);
    }

    internal void Attach(Node node) {
        if (!attachedNodes.Any(attached => SameNode(attached, node)))
            attachedNodes.Add(node);
    }

    private sealed class NodeCollection(Node owner) : ICollection<Node> {
        public int Count => Snapshot().Count;
        public bool IsReadOnly => true;

        public void Add(Node item) {
            owner.Attach(item);
            item.ReplaceBacking(owner.Backing.Nodes.Add(item.Backing).GetAwaiter().GetResult());
        }

        public void Clear() {
            foreach (var node in Snapshot())
                Remove(node);
            owner.attachedNodes.Clear();
        }

        public bool Contains(Node item) => Snapshot().Any(node => SameNode(node, item));

        public void CopyTo(Node[] array, int arrayIndex) => Snapshot().CopyTo(array, arrayIndex);

        public IEnumerator<Node> GetEnumerator() => Snapshot().GetEnumerator();

        public bool Remove(Node item) {
            if (!Contains(item))
                return false;

            var hasOtherEdges = item.Backing.Edges.CountAsync().GetAwaiter().GetResult() > 1;
            if (hasOtherEdges)
                owner.Backing.Nodes.Remove(item.Backing).GetAwaiter().GetResult();
            else
                item.Backing.Delete().GetAwaiter().GetResult();
            owner.attachedNodes.RemoveAll(node => SameNode(node, item));
            return true;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private List<Node> Snapshot() {
            var nodes = new List<Node>(owner.attachedNodes);
            foreach (var state in owner.Backing.Nodes.ToArrayAsync().GetAwaiter().GetResult()) {
                if (!nodes.Any(node => ReferenceEquals(node.Backing, state) || node.GlobalId == state.GlobalId))
                    nodes.Add(new Node(state));
            }
            return nodes;
        }
    }

    internal void ReplaceBacking(NodeBacking backing) {
        if (!ReferenceEquals(Backing, backing))
            Backing = backing;

        MaterializeAttachedNodes();
    }

    internal void MaterializeAttachedNodes() {
        if (materializingAttachedNodes)
            return;

        materializingAttachedNodes = true;
        try {
            foreach (var node in attachedNodes.ToArray())
                node.ReplaceBacking(Backing.Nodes.Add(node.Backing).GetAwaiter().GetResult());
        } finally {
            materializingAttachedNodes = false;
        }
    }

    internal IDictionary<string, string>? CopyAttributesForMaterialization() {
        return Backing.Attributes.Count == 0
            ? null
            : new Dictionary<string, string>(Backing.Attributes, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameNode(Node left, Node right) =>
        ReferenceEquals(left, right)
        || ReferenceEquals(left.Backing, right.Backing)
        || left.GlobalId == right.GlobalId;

    private sealed class EdgeCollection(IAsyncCollection<EdgeBacking> states) : ICollection<Edge> {
        public int Count => states.CountAsync().Result;
        public bool IsReadOnly => false;

        public void Add(Edge item) => item.Backing = states.Add(item.Backing).GetAwaiter().GetResult();

        public void Clear() => states.Clear().GetAwaiter().GetResult();

        public bool Contains(Edge item) {
            var state = item.Backing;
            return states.AnyAsync(candidate =>
                candidate.Node1.GlobalId == state.Node1.GlobalId
                && candidate.Node2.GlobalId == state.Node2.GlobalId)
                .Result;
        }

        public void CopyTo(Edge[] array, int arrayIndex) => states.Select(static state => new Edge(state)).ToArrayAsync().Result.CopyTo(array, arrayIndex);

        public IEnumerator<Edge> GetEnumerator() => states.ToArrayAsync().Result.Select(static state => new Edge(state)).GetEnumerator();

        public bool Remove(Edge item) {
            states.Remove(item.Backing).GetAwaiter().GetResult();
            return true;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
