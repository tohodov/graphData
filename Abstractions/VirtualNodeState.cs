namespace Abstractions;

internal sealed class VirtualNodeState : NodeBacking {
    internal VirtualNodeState(NodeLocalId id) : this() => LocalId = id;
    VirtualNodeState() {
        Edges = new VirtualEdgeCollection(this);
        Nodes = new VirtualNodeCollection(this);
        Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public override NodeLocalId LocalId { get; }
    public override InternalId GlobalId => field ??= new(LocalId, NodeLocalId.Random());
    public override IAsyncCollection<EdgeBacking> Edges { get; }
    public override IAsyncCollection<NodeBacking> Nodes { get; }
    public override IDictionary<string, string> Attributes { get; set; }

    private async Task ConnectTo(NodeBacking target) {
        if (target.GlobalId == GlobalId)
            return;
        if (await Edges.AnyAsync(edge => Connects(edge, GlobalId, target.GlobalId)))
            return;
        var edge = new InMemoryEdgeBacking(this, target);
        await AddEdgeDirect(edge);
        if (target is VirtualNodeState virtualTarget)
            await virtualTarget.AddEdgeDirect(edge);
    }

    private async Task DisconnectFrom(NodeBacking target) {
        if (target is VirtualNodeState virtualTarget)
            await Edges.Remove(await Edges.SingleAsync(x => x.Node1.GlobalId == target.GlobalId || x.Node2.GlobalId == target.GlobalId));
        else
            throw new NotImplementedException();
    }

    private async Task AddEdge(EdgeBacking edge) {
        var other = GetOtherEndpoint(edge, this);
        await ConnectTo(other);
    }

    private async Task AddEdgeDirect(EdgeBacking edge) {
        if (await Edges.AnyAsync(existing => Connects(existing, edge.Node1.GlobalId, edge.Node2.GlobalId)))
            return;
        await Edges.Add(edge);
    }

    private async Task RemoveEdge(EdgeBacking edge) {
        var other = GetOtherEndpoint(edge, this);
        await DisconnectFrom(other);
    }

    private static bool Connects(EdgeBacking edge, InternalId first, InternalId second) =>
        edge.Node1.GlobalId == first && edge.Node2.GlobalId == second
        || edge.Node1.GlobalId == second && edge.Node2.GlobalId == first;

    private static NodeBacking GetOtherEndpoint(EdgeBacking edge, NodeBacking owner) {
        if (edge.Node1.GlobalId == owner.GlobalId)
            return edge.Node2;
        if (edge.Node2.GlobalId == owner.GlobalId)
            return edge.Node1;

        throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
    }

    private sealed class VirtualNodeCollection(VirtualNodeState owner) : IAsyncCollection<NodeBacking> {
        ICollection<NodeBacking> innerCollection = new List<NodeBacking>();

        public async Task<NodeBacking> Add(NodeBacking item) {
            innerCollection.Add(item);
            await owner.ConnectTo(item);
            return item;
        }
        public Task Remove(NodeBacking item) {
            innerCollection.Remove(item);
            return owner.DisconnectFrom(item);
        }
        public async Task Clear() {
            innerCollection.Clear();
            await foreach (var node in owner.Nodes)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(NodeBacking item) => innerCollection.Any(node => node.GlobalId == item.GlobalId);
        IAsyncEnumerator<NodeBacking> IAsyncEnumerable<NodeBacking>.GetAsyncEnumerator(CancellationToken t) => innerCollection.ToAsyncEnumerable().GetAsyncEnumerator(t);
    }

    private sealed class VirtualEdgeCollection(VirtualNodeState owner) : IAsyncCollection<EdgeBacking> {
        ICollection<EdgeBacking> innerCollection = new List<EdgeBacking>();

        public async Task<EdgeBacking> Add(EdgeBacking item) {
            innerCollection.Add(item);
            await owner.AddEdge(item);
            return item;
        }
        public Task Remove(EdgeBacking item) {
            innerCollection.Remove(item);
            return owner.RemoveEdge(item);
        }
        public async Task Clear() {
            innerCollection.Clear();
            await foreach (var node in owner.Nodes)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(EdgeBacking item) => innerCollection.Any(edge => Connects(edge, item.Node1.GlobalId, item.Node2.GlobalId));

        IAsyncEnumerator<EdgeBacking> IAsyncEnumerable<EdgeBacking>.GetAsyncEnumerator(CancellationToken t) => innerCollection.ToAsyncEnumerable().GetAsyncEnumerator(t);
    }
}
