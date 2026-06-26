namespace Abstractions;

internal sealed class VirtualNodeState : NodeState {
    readonly List<EdgeState> edges = [];

    internal VirtualNodeState(NodeLocalId id) : this() => LocalId = id;
    VirtualNodeState() {
        Edges = new VirtualEdgeCollection(this);
        Nodes = new VirtualNodeCollection(this);
        Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public override NodeLocalId LocalId { get; }
    public override InternalId GlobalId => field ??= new(LocalId, NodeLocalId.Random());//TODO кажется вообще не нужно для VirtualNode
    public override ICollection<EdgeState> Edges { get; }
    public override ILazyCollection<NodeState> Nodes { get; }
    public override IDictionary<string, string> Attributes { get; set; }


    private IReadOnlyCollection<EdgeState> EdgeSnapshot() => edges.ToArray();

    private IReadOnlyCollection<NodeState> NodeSnapshot() =>
        edges
            .Select(edge => GetOtherEndpoint(edge, this))
            .Where(node => node.GlobalId != GlobalId)
            .DistinctBy(static node => node.GlobalId)
            .ToArray();

    private void ConnectTo(NodeState target) {
        if (target.GlobalId == GlobalId)
            return;
        if (edges.Any(edge => Connects(edge, GlobalId, target.GlobalId)))
            return;
        var edge = new EdgeStateReferenced(this, target);
        AddEdgeDirect(edge);
        if (target is VirtualNodeState virtualTarget)
            virtualTarget.AddEdgeDirect(edge);
    }

    private bool DisconnectFrom(NodeState target) {
        var removed = RemoveEdgeDirect(GlobalId, target.GlobalId);
        if (target is VirtualNodeState virtualTarget)
            virtualTarget.RemoveEdgeDirect(GlobalId, target.GlobalId);
        return removed;
    }

    private void AddEdge(EdgeState edge) {
        var other = GetOtherEndpoint(edge, this);
        ConnectTo(other);
    }

    private void AddEdgeDirect(EdgeState edge) {
        if (edges.Any(existing => Connects(existing, edge.Node1.GlobalId, edge.Node2.GlobalId)))
            return;

        edges.Add(edge);
    }

    private bool RemoveEdge(EdgeState edge) {
        var other = GetOtherEndpoint(edge, this);
        return DisconnectFrom(other);
    }

    private bool RemoveEdgeDirect(InternalId first, InternalId second) {
        var count = edges.RemoveAll(edge => Connects(edge, first, second));
        return count > 0;
    }

    private static bool Connects(EdgeState edge, InternalId first, InternalId second) =>
        edge.Node1.GlobalId == first && edge.Node2.GlobalId == second
        || edge.Node1.GlobalId == second && edge.Node2.GlobalId == first;

    private static NodeState GetOtherEndpoint(EdgeState edge, NodeState owner) {
        if (edge.Node1.GlobalId == owner.GlobalId)
            return edge.Node2;
        if (edge.Node2.GlobalId == owner.GlobalId)
            return edge.Node1;

        throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
    }

    private sealed class VirtualNodeCollection(VirtualNodeState owner) : ILazyCollection<NodeState> {
        public int Count => owner.NodeSnapshot().Count;
        public bool IsReadOnly => false;

        public void Add(NodeState item) => owner.ConnectTo(item);

        public void Clear() {
            foreach (var node in owner.NodeSnapshot())
                owner.DisconnectFrom(node);
        }

        public bool Contains(NodeState item) =>
            owner.NodeSnapshot().Any(node => node.GlobalId == item.GlobalId);

        public void CopyTo(NodeState[] array, int arrayIndex) =>
            owner.NodeSnapshot().ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<NodeState> GetEnumerator() =>
            owner.NodeSnapshot().GetEnumerator();

        public bool Remove(NodeState item) => owner.DisconnectFrom(item);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerable<NodeState> Traverse() {
            var visited = new HashSet<InternalId>();
            var stack = new Stack<NodeState>();
            stack.Push(owner);

            while (stack.Count > 0) {
                var node = stack.Pop();
                if (!visited.Add(node.GlobalId))
                    continue;

                yield return node;

                if (node is VirtualNodeState virtualNode)
                    foreach (var neighbor in virtualNode.Nodes)
                        if (!visited.Contains(neighbor.GlobalId))
                            stack.Push(neighbor);

                await Task.Yield();
            }
        }
    }

    private sealed class VirtualEdgeCollection(VirtualNodeState owner) : ICollection<EdgeState> {
        public int Count => owner.EdgeSnapshot().Count;
        public bool IsReadOnly => false;

        public void Add(EdgeState item) => owner.AddEdge(item);

        public void Clear() {
            foreach (var node in owner.NodeSnapshot())
                owner.DisconnectFrom(node);
        }

        public bool Contains(EdgeState item) => owner.EdgeSnapshot().Any(edge => Connects(edge, item.Node1.GlobalId, item.Node2.GlobalId));

        public void CopyTo(EdgeState[] array, int arrayIndex) => owner.EdgeSnapshot().ToArray().CopyTo(array, arrayIndex);

        public IEnumerator<EdgeState> GetEnumerator() => owner.EdgeSnapshot().GetEnumerator();

        public bool Remove(EdgeState item) => owner.RemoveEdge(item);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
