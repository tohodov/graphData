using System.Diagnostics.CodeAnalysis;
using Abstractions;
using InternalId = Abstractions.NodeRef.InternalId;

namespace SemanticStorage;

internal sealed class SemanticFileNodeBacking : NodeBacking, ISemanticNodeBacking
{
    private readonly LiveAttributeDictionary attributes;

    internal SemanticFileNodeBacking(
        SemanticFileGraphStorage storage,
        InternalId id,
        SemanticElementKind kind,
        int? memberIndex = null)
    {
        Storage = storage;
        GlobalId = id;
        SemanticKind = kind;
        MemberIndex = memberIndex;
        LocalId = id.LastOrDefault();
        Nodes = new ProjectedNodeCollection(this);
        Edges = new ProjectedEdgeCollection(this);
        attributes = new LiveAttributeDictionary(this);
    }

    internal SemanticFileGraphStorage Storage { get; }

    internal int? MemberIndex { get; }

    internal bool IsVirtualMember => SemanticKind == SemanticElementKind.RelationMember;

    public override NodeLocalId LocalId { get; }

    public override InternalId GlobalId { get; }

    public override IAsyncCollection<EdgeBacking> Edges { get; }

    public override IAsyncCollection<NodeBacking> Nodes { get; }

    public override IDictionary<string, string> Attributes {
        get => attributes;
        set => Storage.ReplaceAttributes(GlobalId, SemanticKind, value);
    }

    public SemanticElementKind SemanticKind { get; }

    public IReadOnlyCollection<InternalId> DeclaredTypeIds =>
        SemanticKind == SemanticElementKind.RelationMember
            ? RelationMembers
                .Select(static member => member.MemberTypeId)
                .Where(static id => id.Any())
                .Distinct()
                .ToArray()
            : Storage.ReadDeclaredTypeIds(GlobalId);

    public IReadOnlyCollection<SemanticRelationMember> RelationMembers =>
        Storage.ReadRelationMembers(this);

    internal override Task Delete() => Storage.Delete(this);

    private sealed class ProjectedNodeCollection(SemanticFileNodeBacking owner) : IAsyncCollection<NodeBacking>
    {
        public Task<NodeBacking> Add(NodeBacking item) => owner.Storage.AddProjectedNodeAsync(owner, item);

        public Task Remove(NodeBacking item) => owner.Storage.RemoveProjectedNodeAsync(owner, item);

        public Task Clear() => owner.Storage.ClearProjectedNodesAsync(owner);

        public async Task<bool> Contains(NodeBacking item) =>
            await owner.Storage.GetProjectedNeighborsAsync(owner)
                .AnyAsync(candidate => candidate.GlobalId == item.GlobalId)
                .ConfigureAwait(false);

        public IAsyncEnumerator<NodeBacking> GetAsyncEnumerator(CancellationToken cancellationToken) =>
            owner.Storage.GetProjectedNeighborsAsync(owner, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
    }

    private sealed class ProjectedEdgeCollection(SemanticFileNodeBacking owner) : IAsyncCollection<EdgeBacking>
    {
        public async Task<EdgeBacking> Add(EdgeBacking item)
        {
            var other = GetOtherEndpoint(item);
            await owner.Storage.Connect(owner.GlobalId, other.GlobalId).ConfigureAwait(false);
            return new InMemoryEdgeBacking(owner, other);
        }

        public Task Remove(EdgeBacking item) =>
            owner.Storage.Disconnect(owner.GlobalId, GetOtherEndpoint(item).GlobalId);

        public Task Clear() => owner.Storage.ClearProjectedNodesAsync(owner);

        public async Task<bool> Contains(EdgeBacking item)
        {
            var other = GetOtherEndpoint(item);
            return await owner.Storage.GetProjectedNeighborsAsync(owner)
                .AnyAsync(candidate => candidate.GlobalId == other.GlobalId)
                .ConfigureAwait(false);
        }

        public async IAsyncEnumerator<EdgeBacking> GetAsyncEnumerator(
            CancellationToken cancellationToken)
        {
            await foreach (var neighbor in owner.Storage
                               .GetProjectedNeighborsAsync(owner, cancellationToken)
                               .ConfigureAwait(false))
                yield return new InMemoryEdgeBacking(owner, neighbor);
        }

        private NodeBacking GetOtherEndpoint(EdgeBacking edge)
        {
            if (edge.Node1.GlobalId == owner.GlobalId)
                return edge.Node2;
            if (edge.Node2.GlobalId == owner.GlobalId)
                return edge.Node1;
            throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
        }
    }

    private sealed class LiveAttributeDictionary(SemanticFileNodeBacking owner) : IDictionary<string, string>
    {
        public ICollection<string> Keys => Snapshot().Keys;

        public ICollection<string> Values => Snapshot().Values;

        public int Count => Snapshot().Count;

        public bool IsReadOnly => false;

        public string this[string key] {
            get => Snapshot()[key];
            set => Mutate(values => values[key] = value);
        }

        public void Add(string key, string value) => Mutate(values => values.Add(key, value));

        public bool ContainsKey(string key) => Snapshot().ContainsKey(key);

        public bool Remove(string key)
        {
            if (!ContainsKey(key))
                return false;
            Mutate(values => values.Remove(key));
            return true;
        }

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
            Snapshot().TryGetValue(key, out value);

        public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

        public void Clear() => owner.Storage.ReplaceAttributes(
            owner.GlobalId,
            owner.SemanticKind,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public bool Contains(KeyValuePair<string, string> item) =>
            ((ICollection<KeyValuePair<string, string>>)Snapshot()).Contains(item);

        public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<string, string>>)Snapshot()).CopyTo(array, arrayIndex);

        public bool Remove(KeyValuePair<string, string> item)
        {
            if (!Contains(item))
                return false;
            return Remove(item.Key);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Snapshot().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private Dictionary<string, string> Snapshot() =>
            new(owner.Storage.ReadAttributes(owner.GlobalId, owner.SemanticKind), StringComparer.OrdinalIgnoreCase);

        private void Mutate(Action<Dictionary<string, string>> mutation)
        {
            var values = Snapshot();
            mutation(values);
            owner.Storage.ReplaceAttributes(owner.GlobalId, owner.SemanticKind, values);
        }
    }
}
