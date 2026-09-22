namespace Abstractions;

internal sealed class VirtualNodeState : CarrierNodeBacking {
    internal VirtualNodeState(NodeLocalId id) : this() => LocalId = id;
    VirtualNodeState() {
        Edges = new VirtualEdgeCollection(this);
        Nodes = new VirtualNodeCollection(this);
        Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public override NodeLocalId LocalId { get; }
    public override InternalId GlobalId => field ??= new(LocalId, NodeLocalId.Random());
    public override IAsyncCollection<CarrierEdgeBacking> Edges { get; }
    public override IAsyncCollection<CarrierNodeBacking> Nodes { get; }
    public override IDictionary<string, string> Attributes { get; set; }

    private async Task ConnectTo(CarrierNodeBacking target) {
        if (target.GlobalId == GlobalId)
            return;
        if (await Edges.AnyAsync(edge => Connects(edge, GlobalId, target.GlobalId)))
            return;
        var edge = new InMemoryEdgeBacking(this, target);
        await AddEdgeDirect(edge);
        if (target is VirtualNodeState virtualTarget)
            await virtualTarget.AddEdgeDirect(edge);
    }

    private async Task DisconnectFrom(CarrierNodeBacking target) {
        if (target is VirtualNodeState virtualTarget)
            await Edges.Remove(await Edges.SingleAsync(x => x.Node1.GlobalId == target.GlobalId || x.Node2.GlobalId == target.GlobalId));
        else
            throw new NotImplementedException();
    }

    private async Task AddEdge(CarrierEdgeBacking edge) {
        var other = GetOtherEndpoint(edge, this);
        await ConnectTo(other);
    }

    private async Task AddEdgeDirect(CarrierEdgeBacking edge) {
        if (await Edges.AnyAsync(existing => Connects(existing, edge.Node1.GlobalId, edge.Node2.GlobalId)))
            return;
        await Edges.Add(edge);
    }

    private async Task RemoveEdge(CarrierEdgeBacking edge) {
        var other = GetOtherEndpoint(edge, this);
        await DisconnectFrom(other);
    }

    private static bool Connects(CarrierEdgeBacking edge, InternalId first, InternalId second) =>
        edge.Node1.GlobalId == first && edge.Node2.GlobalId == second
        || edge.Node1.GlobalId == second && edge.Node2.GlobalId == first;

    private static CarrierNodeBacking GetOtherEndpoint(CarrierEdgeBacking edge, CarrierNodeBacking owner) {
        if (edge.Node1.GlobalId == owner.GlobalId)
            return edge.Node2;
        if (edge.Node2.GlobalId == owner.GlobalId)
            return edge.Node1;

        throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
    }

    private sealed class VirtualNodeCollection(VirtualNodeState owner) : IAsyncCollection<CarrierNodeBacking> {
        ICollection<CarrierNodeBacking> innerCollection = new List<CarrierNodeBacking>();

        public async Task<CarrierNodeBacking> Add(CarrierNodeBacking item) {
            innerCollection.Add(item);
            await owner.ConnectTo(item);
            return item;
        }
        public Task Remove(CarrierNodeBacking item) {
            innerCollection.Remove(item);
            return owner.DisconnectFrom(item);
        }
        public async Task Clear() {
            innerCollection.Clear();
            await foreach (var node in owner.Nodes)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(CarrierNodeBacking item) => innerCollection.Any(node => node.GlobalId == item.GlobalId);
        IAsyncEnumerator<CarrierNodeBacking> IAsyncEnumerable<CarrierNodeBacking>.GetAsyncEnumerator(CancellationToken t) => innerCollection.ToAsyncEnumerable().GetAsyncEnumerator(t);
    }

    private sealed class VirtualEdgeCollection(VirtualNodeState owner) : IAsyncCollection<CarrierEdgeBacking> {
        ICollection<CarrierEdgeBacking> innerCollection = new List<CarrierEdgeBacking>();

        public async Task<CarrierEdgeBacking> Add(CarrierEdgeBacking item) {
            innerCollection.Add(item);
            await owner.AddEdge(item);
            return item;
        }
        public Task Remove(CarrierEdgeBacking item) {
            innerCollection.Remove(item);
            return owner.RemoveEdge(item);
        }
        public async Task Clear() {
            innerCollection.Clear();
            await foreach (var node in owner.Nodes)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(CarrierEdgeBacking item) => innerCollection.Any(edge => Connects(edge, item.Node1.GlobalId, item.Node2.GlobalId));

        IAsyncEnumerator<CarrierEdgeBacking> IAsyncEnumerable<CarrierEdgeBacking>.GetAsyncEnumerator(CancellationToken t) => innerCollection.ToAsyncEnumerable().GetAsyncEnumerator(t);
    }
}
