using Abstractions;

public class Node {
    readonly NodeCollection nodes;

    internal NodeBacking Backing { get; private set; }

    public virtual NodeLocalId LocalId => Backing.LocalId;
    public virtual InternalId GlobalId => Backing.GlobalId;

    public virtual ICollection<Edge> Edges => new EdgeCollection(Backing.Edges);
    public virtual ICollection<Incidence> Incidences { get; } = new List<Incidence>();
    public virtual NodeCollection Nodes => nodes;

    public virtual IDictionary<string, string> Attributes {
        get => Backing.Attributes;
        set => Backing.Attributes = value;
    }

    public Node(NodeLocalId localId) : this(new VirtualNodeState(localId)) { }

    internal Node(NodeBacking state) {
        Backing = state;
        nodes = new NodeCollection(this);
    }

    internal void Attach(Incidence incidence) {
        if (Incidences.Contains(incidence))
            return;
        Incidences.Add(incidence);
    }

    public sealed class NodeCollection : ICollection<Node> {
        readonly Node owner;

        // Keeps object identity and runtime type for nodes created from virtual input.
        // Existing links remain owned by backing and are merged in Snapshot().
        readonly List<Node> createdLinkedNodes = [];
        bool materializingPreparedLinks;

        internal NodeCollection(Node owner) => this.owner = owner;

        public int Count => Snapshot().Count;
        public bool IsReadOnly => true;

        public void Add(Node item) {
            if (Contains(item) || WouldCreateDuplicateVirtualNode(item))
                throw new InvalidOperationException($"Node '{item.GlobalId}' is already linked to '{owner.GlobalId}'.");

            if (item.Backing.IsVirtual)
                TrackCreatedNode(item);

            var backing = owner.Backing.Nodes.Add(item.Backing).GetAwaiter().GetResult();
            item.ReplaceBacking(backing);
        }

        public void Clear() {
            foreach (var node in Snapshot())
                Remove(node);
            createdLinkedNodes.Clear();
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
            createdLinkedNodes.RemoveAll(node => SameNode(node, item));
            return true;
        }

        internal void StageVirtualNode(Node item) {
            if (!item.Backing.IsVirtual)
                throw new ArgumentException("Only virtual nodes can be staged for creation.", nameof(item));
            if (WouldCreateDuplicateVirtualNode(item))
                throw new InvalidOperationException($"Node '{item.LocalId}' is already linked to '{owner.GlobalId}'.");

            TrackCreatedNode(item);
        }

        internal IReadOnlyCollection<Node> GetPreparedNodes() {
            var nodes = new List<Node>();
            if (owner.Backing.IsVirtual)
                foreach (var state in owner.Backing.Nodes.ToArrayAsync().GetAwaiter().GetResult())
                    AddPreparedNode(nodes, ResolveNode(state));

            foreach (var node in createdLinkedNodes.Where(static node => node.Backing.IsVirtual))
                AddPreparedNode(nodes, node);

            return nodes;
        }

        internal void MaterializePreparedLinks(NodeBacking previousBacking) {
            if (materializingPreparedLinks || owner.Backing.IsVirtual)
                return;

            materializingPreparedLinks = true;
            try {
                foreach (var node in GetPreparedNodes(previousBacking))
                    node.ReplaceBacking(owner.Backing.Nodes.Add(node.Backing).GetAwaiter().GetResult());
            } finally {
                materializingPreparedLinks = false;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private List<Node> Snapshot() {
            var nodes = new List<Node>(createdLinkedNodes);
            foreach (var state in owner.Backing.Nodes.ToArrayAsync().GetAwaiter().GetResult()) {
                if (!nodes.Any(node => ReferenceEquals(node.Backing, state) || node.GlobalId == state.GlobalId))
                    nodes.Add(new Node(state));
            }
            return nodes;
        }

        private IReadOnlyCollection<Node> GetPreparedNodes(NodeBacking previousBacking) {
            var nodes = new List<Node>();
            if (previousBacking.IsVirtual)
                foreach (var state in previousBacking.Nodes.ToArrayAsync().GetAwaiter().GetResult())
                    AddPreparedNode(nodes, ResolveNode(state));

            foreach (var node in createdLinkedNodes.Where(static node => node.Backing.IsVirtual))
                AddPreparedNode(nodes, node);

            return nodes;
        }

        private void TrackCreatedNode(Node item) {
            if (!createdLinkedNodes.Any(node => SameNode(node, item)))
                createdLinkedNodes.Add(item);
        }

        private Node ResolveNode(NodeBacking state) {
            return createdLinkedNodes.FirstOrDefault(node => ReferenceEquals(node.Backing, state) || node.GlobalId == state.GlobalId)
                ?? new Node(state);
        }

        private bool WouldCreateDuplicateVirtualNode(Node item) =>
            item.Backing.IsVirtual && Snapshot().Any(node => node.LocalId == item.LocalId);

        private static void AddPreparedNode(List<Node> nodes, Node node) {
            if (!nodes.Any(existing => SameNode(existing, node)))
                nodes.Add(node);
        }
    }

    internal void ReplaceBacking(NodeBacking backing) {
        var previousBacking = Backing;
        if (!ReferenceEquals(Backing, backing))
            Backing = backing;

        nodes.MaterializePreparedLinks(previousBacking);
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
