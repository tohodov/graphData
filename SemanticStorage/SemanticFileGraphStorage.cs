using System.Runtime.CompilerServices;
using System.Text.Json;
using Abstractions;
using Microsoft.Extensions.Options;
using InternalId = Abstractions.NodeRef.InternalId;
using NodePath = Abstractions.NodeRef.NodePath;

namespace SemanticStorage;

/// <summary>
/// Stores semantic instances and relations as canonical JSON documents. The
/// <see cref="IGraphStorage"/> implementation is a compatibility projection;
/// projected edges are never persisted as a second graph.
/// </summary>
internal sealed class SemanticFileGraphStorage : IGraphStorage, ISemanticGraphStorage
{
    internal const string TypeRecordFileName = "type.json";
    internal const string InstanceRecordFileName = "instance.json";
    internal const string IncidentRecordFileName = "relations.json";
    // '@' is rejected by NodeNameValidator for canonical address segments, so
    // compatibility-only relation members occupy a structurally disjoint id
    // namespace rather than relying on order-dependent collision checks.
    internal const string VirtualMemberPrefix = "@member-";
    private const int FileGateCount = 257;

    private static readonly NodeLocalId TypeRootLocalId = new("NodeTypes");
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly string typesPath;
    private readonly string instancesPath;
    private readonly string incidentsPath;
    private readonly ICancellationTokenAccessor cancellationTokens;
    // A fixed stripe set keeps synchronization memory bounded even when the
    // storage contains an unbounded number of record paths.
    private readonly SemaphoreSlim[] fileGates = Enumerable
        .Range(0, FileGateCount)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly SemaphoreSlim typeMutationGate = new(1, 1);
    private readonly SemaphoreSlim instanceMutationGate = new(1, 1);
    private readonly SemaphoreSlim incidenceGate = new(1, 1);

    public NodeBacking Root { get; }

    public NodeBacking TypeRoot { get; }

    public SemanticFileGraphStorage(
        IOptions<SemanticGraphStorageOptions> options,
        ICancellationTokenAccessor cancellationTokens)
    {
        var value = options.Value;
        rootPath = Path.GetFullPath(value.RootPath);
        typesPath = ResolveConfiguredChild(rootPath, value.TypesDirectoryName, nameof(value.TypesDirectoryName));
        instancesPath = ResolveConfiguredChild(rootPath, value.InstancesDirectoryName, nameof(value.InstancesDirectoryName));
        var indexesPath = ResolveConfiguredChild(rootPath, value.IndexesDirectoryName, nameof(value.IndexesDirectoryName));
        incidentsPath = Path.Combine(indexesPath, "incidents");
        this.cancellationTokens = cancellationTokens;

        Directory.CreateDirectory(typesPath);
        Directory.CreateDirectory(instancesPath);
        Directory.CreateDirectory(incidentsPath);

        Root = new SemanticFileNodeBacking(this, new InternalId(), SemanticElementKind.Root);
        TypeRoot = new SemanticFileNodeBacking(
            this,
            new InternalId(TypeRootLocalId),
            SemanticElementKind.TypeCatalog);

    }

    public bool IsType(NodeBacking node)
    {
        if (node is not SemanticFileNodeBacking semantic || !ReferenceEquals(semantic.Storage, this))
            return false;
        if (semantic.SemanticKind != SemanticElementKind.Type)
            return false;
        return ReadTypeRecordAsync(semantic.GlobalId).GetAwaiter().GetResult() is { Kind: "type" };
    }

    public IReadOnlyCollection<InternalId> GetDeclaredTypeIds(NodeBacking node)
    {
        if (node is not SemanticFileNodeBacking semantic || !ReferenceEquals(semantic.Storage, this))
            return Array.Empty<InternalId>();
        return ReadDeclaredTypeIdsAsync(semantic.GlobalId).GetAwaiter().GetResult();
    }

    public IReadOnlyCollection<InternalId> GetRequiredTypeIds(NodeBacking type)
    {
        if (type is not SemanticFileNodeBacking semantic || !ReferenceEquals(semantic.Storage, this))
            return Array.Empty<InternalId>();
        var record = ReadTypeRecordAsync(semantic.GlobalId).GetAwaiter().GetResult();
        return record?.RequiredTypeIds.Select(ParseId).ToArray() ?? Array.Empty<InternalId>();
    }

    public async Task SetDeclaredTypeIdsAsync(NodeRef node, IEnumerable<NodeRef> typeIds)
    {
        var nodeId = ToInternalId(node);
        var normalizedTypeIds = NormalizeReferenceIds(typeIds);

        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            await MutateInstanceRecordAsync(
                nodeId,
                record => record with { TypeIds = normalizedTypeIds }).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    public async Task SetRequiredTypeIdsAsync(NodeRef type, IEnumerable<NodeRef> requiredTypeIds)
    {
        var typeId = ToInternalId(type);
        var normalizedRequiredIds = NormalizeReferenceIds(requiredTypeIds);

        await typeMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            var record = await ReadTypeRecordAsync(typeId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Type '{typeId}' was not found.");
            if (ParseTypeKind(record, typeId) != SemanticElementKind.Type)
                throw new InvalidOperationException(
                    $"Type infrastructure element '{typeId}' cannot declare required types.");
            await MutateTypeRecordAsync(
                typeId,
                current => current with { RequiredTypeIds = normalizedRequiredIds }).ConfigureAwait(false);
        } finally {
            typeMutationGate.Release();
        }
    }

    public async Task<NodeBacking> CreateRelationAsync(
        NodeLocalId localId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes = null)
    {
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            return await CreateRelationCoreAsync(localId, typeIds, members, attributes).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task<NodeBacking> CreateRelationCoreAsync(
        NodeLocalId localId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes)
    {
        ValidateSegment(localId, "Relation local id");
        if (localId == TypeRootLocalId)
            throw new InvalidOperationException($"Relation local id '{localId}' is reserved.");

        var relationId = new InternalId(localId);
        var record = await BuildRelationRecordAsync(relationId, typeIds, members, attributes).ConfigureAwait(false);

        if (await ReadInstanceRecordAsync(relationId).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"Semantic record '{relationId}' already exists.");
        await EnsureAcyclicRelationDependenciesAsync(relationId, record).ConfigureAwait(false);
        await EnsureVirtualMemberAddressesAvailableAsync(relationId, record).ConfigureAwait(false);

        var selfMarkerCreated = await EnsureIncidentMarkerAsync(relationId).ConfigureAwait(false);
        var indexMayContainEntries = false;
        try {
            indexMayContainEntries = true;
            await AddRelationToIncidenceIndexAsync(record).ConfigureAwait(false);
            // Publish all reverse lookups before the canonical relation. A
            // crash may leave stale index entries, which reads verify against
            // the relation document, but cannot leave an undiscoverable
            // canonical relation.
            await CreateInstanceRecordAsync(relationId, record).ConfigureAwait(false);
        } catch {
            if (indexMayContainEntries)
                await RemoveRelationFromIncidenceIndexAsync(record).ConfigureAwait(false);
            if (selfMarkerCreated)
                await DeleteIncidentMarkerAsync(relationId).ConfigureAwait(false);
            throw;
        }
        return new SemanticFileNodeBacking(this, relationId, SemanticElementKind.Relation);
    }

    public async Task<NodeBacking> ReplaceRelationAsync(
        NodeRef relationId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes = null)
    {
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            var id = ToInternalId(relationId);
            var current = await ReadInstanceRecordAsync(id).ConfigureAwait(false);
            if (current is not { Kind: "relation" })
                throw new InvalidOperationException($"Semantic relation '{id}' was not found.");
            _ = await ReadRequiredIncidentEntriesAsync(id, CancellationToken.None).ConfigureAwait(false);

            var replacement = await BuildRelationRecordAsync(id, typeIds, members, attributes).ConfigureAwait(false);
            await EnsureAcyclicRelationDependenciesAsync(id, replacement).ConfigureAwait(false);
            await EnsureVirtualMemberAddressesAvailableAsync(id, replacement).ConfigureAwait(false);
            try {
                await AddRelationToIncidenceIndexAsync(replacement).ConfigureAwait(false);
                await MutateInstanceRecordAsync(id, _ => replacement).ConfigureAwait(false);
            } catch {
                await ReconcileRelationIncidenceIndexAsync(current, replacement).ConfigureAwait(false);
                throw;
            }

            // Old entries are harmless while present because every read
            // verifies them against the canonical document. Remove them only
            // after the atomic canonical replacement is visible.
            await ReconcileRelationIncidenceIndexAsync(replacement, current).ConfigureAwait(false);
            return new SemanticFileNodeBacking(this, id, SemanticElementKind.Relation);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task<SemanticInstanceFileRecord> BuildRelationRecordAsync(
        InternalId relationId,
        IReadOnlyCollection<NodeRef> typeIds,
        IReadOnlyCollection<SemanticRelationMember> members,
        IDictionary<string, string>? attributes)
    {
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        var memberRecords = new List<SemanticRelationMemberFileRecord>(members.Count);
        foreach (var member in members) {
            var memberName = RequireMemberName(member.Name);
            var memberTypeId = FormatId(member.MemberTypeId);
            if (!memberNames.Add(memberName))
                throw new InvalidOperationException($"Relation member name '{memberName}' is declared more than once.");

            var participantIds = member.ParticipantIds.Select(FormatId).ToArray();
            if (participantIds.Distinct(StringComparer.Ordinal).Count() != participantIds.Length)
                throw new InvalidOperationException($"Relation member '{memberName}' contains a participant more than once.");
            foreach (var participantId in member.ParticipantIds) {
                var participant = await Get(participantId).ConfigureAwait(false);
                if (participant is not ISemanticNodeBacking {
                        SemanticKind: SemanticElementKind.Instance or SemanticElementKind.Relation
                    })
                    throw new InvalidOperationException(
                        $"Relation participant '{participantId}' is outside the instance graph.");
            }

            memberRecords.Add(new SemanticRelationMemberFileRecord {
                Name = memberName,
                MemberTypeId = memberTypeId,
                ParticipantIds = participantIds
            });
        }
        var relationTypeIds = typeIds
            .Select(ToInternalId)
            .Select(FormatId)
            .Where(static id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var record = new SemanticInstanceFileRecord {
            Id = FormatId(relationId),
            Kind = "relation",
            TypeIds = relationTypeIds,
            Attributes = CopyAttributes(attributes),
            Members = memberRecords.ToArray()
        };
        return record;
    }

    private async Task EnsureAcyclicRelationDependenciesAsync(
        InternalId relationId,
        SemanticInstanceFileRecord prospectiveRecord)
    {
        var targetId = FormatId(relationId);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(prospectiveRecord.Members
            .SelectMany(static member => member.ParticipantIds)
            .Reverse());
        while (pending.TryPop(out var participantId)) {
            if (string.Equals(participantId, targetId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Higher-order relation dependency cycle reaches '{targetId}'.");
            if (!visited.Add(participantId))
                continue;

            var participant = await ReadInstanceRecordAsync(ParseId(participantId)).ConfigureAwait(false);
            if (participant is not { Kind: "relation" })
                continue;
            foreach (var dependencyId in participant.Members
                         .SelectMany(static member => member.ParticipantIds)
                         .Reverse())
                pending.Push(dependencyId);
        }
    }

    public async Task<SemanticRelation?> GetRelationAsync(NodeRef relationId)
    {
        var id = ToInternalId(relationId);
        var record = await ReadInstanceRecordAsync(id).ConfigureAwait(false);
        return record is { Kind: "relation" }
            ? ToSemanticRelation(id, record)
            : null;
    }

    public async Task<SemanticElementSnapshot?> ReadSemanticElementAsync(
        NodeRef elementId,
        CancellationToken cancellationToken = default)
    {
        var id = ToInternalId(elementId);
        if (IsTypeRoot(id))
            return new SemanticElementSnapshot(
                id,
                SemanticElementKind.TypeCatalog,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<InternalId>(),
                Array.Empty<InternalId>(),
                Array.Empty<SemanticRelationMember>());
        if (IsTypeAddress(id)) {
            var record = await ReadTypeRecordAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return null;
            return new SemanticElementSnapshot(
                id,
                ParseTypeKind(record, id),
                new Dictionary<string, string>(record.Attributes, StringComparer.OrdinalIgnoreCase),
                Array.Empty<InternalId>(),
                record.RequiredTypeIds.Select(ParseId).ToArray(),
                Array.Empty<SemanticRelationMember>());
        }
        if (IsRoot(id))
            return null;

        var instance = await ReadInstanceRecordAsync(id, cancellationToken).ConfigureAwait(false);
        if (instance is null)
            return null;
        return new SemanticElementSnapshot(
            id,
            ParseInstanceKind(instance, id),
            new Dictionary<string, string>(instance.Attributes, StringComparer.OrdinalIgnoreCase),
            instance.TypeIds.Select(ParseId).ToArray(),
            Array.Empty<InternalId>(),
            instance.Members.Select(ToSemanticMember).ToArray());
    }

    public async IAsyncEnumerable<SemanticRelation> FindIncidentRelationsAsync(
        NodeRef participantId,
        NodeRef? relationTypeId = null,
        string? memberName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var participant = ToInternalId(participantId);
        var participantText = FormatId(participant);
        var relationType = relationTypeId is null ? null : FormatId(ToInternalId(relationTypeId));
        var entries = await ReadIncidentEntriesAsync(participant, cancellationToken).ConfigureAwait(false);
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries) {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationTokens.Token.ThrowIfCancellationRequested();
            if (memberName is not null
                && !string.Equals(entry.MemberName, memberName, StringComparison.Ordinal))
                continue;

            var relationId = ParseId(entry.RelationId);
            var record = await ReadInstanceRecordAsync(relationId, cancellationToken).ConfigureAwait(false);
            if (record is not { Kind: "relation" })
                continue;
            if (entry.MemberIndex < 0 || entry.MemberIndex >= record.Members.Count)
                continue;
            var indexedMember = record.Members[entry.MemberIndex];
            if (!string.Equals(indexedMember.Name, entry.MemberName, StringComparison.Ordinal)
                || !indexedMember.ParticipantIds.Contains(participantText, StringComparer.Ordinal))
                continue;
            if (relationType is not null && !record.TypeIds.Contains(relationType, StringComparer.Ordinal))
                continue;
            if (!yielded.Add(record.Id))
                continue;
            yield return ToSemanticRelation(relationId, record);
        }
    }

    public async Task<NodeBacking> Create(
        NodeLocalId name,
        NodeRef? parent = null,
        IDictionary<string, string>? attributes = null)
    {
        var parentId = parent is null ? new InternalId() : ToInternalId(parent);
        return IsTypeAddress(parentId)
            ? await CreateTypeElementAsync(
                name,
                parentId,
                SemanticElementKind.Type,
                Array.Empty<NodeRef>(),
                attributes).ConfigureAwait(false)
            : await CreateInstanceAsync(
                name,
                parent,
                Array.Empty<NodeRef>(),
                attributes).ConfigureAwait(false);
    }

    public async Task<NodeBacking> CreateTypeElementAsync(
        NodeLocalId localId,
        NodeRef parentId,
        SemanticElementKind kind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes = null)
    {
        await typeMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            return await CreateTypeElementCoreAsync(
                localId,
                parentId,
                kind,
                requiredTypeIds,
                attributes).ConfigureAwait(false);
        } finally {
            typeMutationGate.Release();
        }
    }

    private async Task<NodeBacking> CreateTypeElementCoreAsync(
        NodeLocalId localId,
        NodeRef parentId,
        SemanticElementKind kind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes)
    {
        ValidateSegment(localId, "Type local id");
        EnsureTypeElementKind(kind);
        EnsureInfrastructureHasNoRequiredTypes(kind, requiredTypeIds);
        var parent = ToInternalId(parentId);
        if (!IsTypeAddress(parent))
            throw new InvalidOperationException($"Type parent '{parent}' is outside the type catalog.");
        var parentBacking = await Get(parent).ConfigureAwait(false);
        if (parentBacking is not ISemanticNodeBacking {
                SemanticKind: SemanticElementKind.TypeCatalog
                    or SemanticElementKind.Type
                    or SemanticElementKind.TypeInfrastructure
            })
            throw new InvalidOperationException($"Type parent '{parent}' was not found.");

        var id = Append(parent, localId);
        var record = new SemanticTypeFileRecord {
            Id = FormatId(id),
            Kind = kind == SemanticElementKind.Type ? "type" : "infrastructure",
            RequiredTypeIds = NormalizeReferenceIds(requiredTypeIds),
            Attributes = CopyAttributes(attributes)
        };
        await CreateTypeRecordAsync(id, record).ConfigureAwait(false);
        return new SemanticFileNodeBacking(this, id, kind);
    }

    public async Task<NodeBacking> ReplaceTypeElementAsync(
        NodeRef typeId,
        SemanticElementKind expectedKind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes = null)
    {
        await typeMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            return await ReplaceTypeElementCoreAsync(
                typeId,
                expectedKind,
                requiredTypeIds,
                attributes).ConfigureAwait(false);
        } finally {
            typeMutationGate.Release();
        }
    }

    private async Task<NodeBacking> ReplaceTypeElementCoreAsync(
        NodeRef typeId,
        SemanticElementKind expectedKind,
        IReadOnlyCollection<NodeRef> requiredTypeIds,
        IDictionary<string, string>? attributes)
    {
        EnsureTypeElementKind(expectedKind);
        EnsureInfrastructureHasNoRequiredTypes(expectedKind, requiredTypeIds);
        var id = ToInternalId(typeId);
        var current = await ReadTypeRecordAsync(id).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Type element '{id}' was not found.");
        var currentKind = string.Equals(current.Kind, "type", StringComparison.OrdinalIgnoreCase)
            ? SemanticElementKind.Type
            : SemanticElementKind.TypeInfrastructure;
        if (currentKind != expectedKind)
            throw new InvalidOperationException(
                $"Type element '{id}' is a {currentKind}, not the expected {expectedKind}.");

        var replacement = new SemanticTypeFileRecord {
            Id = FormatId(id),
            Kind = expectedKind == SemanticElementKind.Type ? "type" : "infrastructure",
            RequiredTypeIds = NormalizeReferenceIds(requiredTypeIds),
            Attributes = CopyAttributes(attributes)
        };
        await MutateTypeRecordAsync(id, _ => replacement).ConfigureAwait(false);
        return new SemanticFileNodeBacking(this, id, expectedKind);
    }

    public async Task<NodeBacking> CreateInstanceAsync(
        NodeLocalId localId,
        NodeRef? namespacePrefix,
        IReadOnlyCollection<NodeRef> typeIds,
        IDictionary<string, string>? attributes = null)
    {
        ValidateSegment(localId, "Instance local id");
        var parentId = namespacePrefix is null ? new InternalId() : ToInternalId(namespacePrefix);
        if (IsTypeAddress(parentId))
            throw new InvalidOperationException("Instances cannot be created inside the type catalog.");
        var id = Append(parentId, localId);
        if (IsRoot(parentId) && localId == TypeRootLocalId)
            throw new InvalidOperationException($"Instance local id '{localId}' is reserved for the type catalog.");

        var instanceRecord = new SemanticInstanceFileRecord {
            Id = FormatId(id),
            Kind = "instance",
            TypeIds = NormalizeReferenceIds(typeIds),
            Attributes = CopyAttributes(attributes)
        };
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            await EnsureInstanceNamespaceParentAllowedAsync(parentId).ConfigureAwait(false);
            var currentParent = await Get(parentId).ConfigureAwait(false);
            if (currentParent is SemanticFileNodeBacking { SemanticKind: SemanticElementKind.Relation }
                && await IsVirtualMemberLocalIdAsync(parentId, localId).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"Instance address '{id}' is reserved by the relation-member compatibility projection.");
            var markerCreated = await EnsureIncidentMarkerAsync(id).ConfigureAwait(false);
            try {
                await CreateInstanceRecordAsync(id, instanceRecord).ConfigureAwait(false);
            } catch {
                if (markerCreated)
                    await DeleteIncidentMarkerAsync(id).ConfigureAwait(false);
                throw;
            }
        } finally {
            instanceMutationGate.Release();
        }
        return new SemanticFileNodeBacking(this, id, SemanticElementKind.Instance);
    }

    public async Task<NodeBacking> ReplaceInstanceAsync(
        NodeRef instanceId,
        IReadOnlyCollection<NodeRef> typeIds,
        IDictionary<string, string>? attributes = null)
    {
        var id = ToInternalId(instanceId);
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            _ = await ReadRequiredIncidentEntriesAsync(id, CancellationToken.None).ConfigureAwait(false);
            var current = await ReadInstanceRecordAsync(id).ConfigureAwait(false);
            if (current is not { Kind: "instance" })
                throw new InvalidOperationException($"Semantic instance '{id}' was not found.");
            var replacement = new SemanticInstanceFileRecord {
                Id = FormatId(id),
                Kind = "instance",
                TypeIds = NormalizeReferenceIds(typeIds),
                Attributes = CopyAttributes(attributes)
            };
            await MutateInstanceRecordAsync(id, _ => replacement).ConfigureAwait(false);
            return new SemanticFileNodeBacking(this, id, SemanticElementKind.Instance);
        } finally {
            instanceMutationGate.Release();
        }
    }

    public async Task<NodeBacking?> Get(NodeRef path)
    {
        var id = ToInternalId(path);
        if (IsRoot(id))
            return Root;
        if (IsTypeRoot(id))
            return TypeRoot;

        if (IsTypeAddress(id)) {
            var record = await ReadTypeRecordAsync(id).ConfigureAwait(false);
            if (record is not null)
                return new SemanticFileNodeBacking(
                    this,
                    id,
                    ParseTypeKind(record, id));
            return null;
        }

        if (id.Last().ToString().StartsWith(VirtualMemberPrefix, StringComparison.Ordinal))
            return await TryGetVirtualMemberAsync(id).ConfigureAwait(false);

        var instance = await ReadInstanceRecordAsync(id).ConfigureAwait(false);
        if (instance is not null)
            return new SemanticFileNodeBacking(
                this,
                id,
                ParseInstanceKind(instance, id));

        return null;
    }

    public async Task<NodeBacking?> Get(NodeRef parent, NodeLocalId nodeId)
    {
        var parentId = ToInternalId(parent);
        var childId = Append(parentId, nodeId);
        return await Get(childId).ConfigureAwait(false);
    }

    public async Task Update(NodeRef path, IDictionary<string, string> attributes)
    {
        var id = ToInternalId(path);
        await ReplaceAttributesAsync(id, attributes).ConfigureAwait(false);
    }

    public Task Delete(NodeBacking node)
    {
        if (node is not SemanticFileNodeBacking semantic || !ReferenceEquals(semantic.Storage, this))
            throw new ArgumentException("Node does not belong to this storage.", nameof(node));
        return DeleteCoreAsync(semantic);
    }

    public async Task Delete(NodeRef path)
    {
        var node = await Get(path).ConfigureAwait(false);
        if (node is not null)
            await Delete(node).ConfigureAwait(false);
    }

    public async Task Connect(NodeRef sourcePath, NodeRef targetPath)
    {
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            await ConnectCoreAsync(sourcePath, targetPath).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task ConnectCoreAsync(NodeRef sourcePath, NodeRef targetPath)
    {
        var source = await Get(sourcePath).ConfigureAwait(false) as SemanticFileNodeBacking;
        var target = await Get(targetPath).ConfigureAwait(false) as SemanticFileNodeBacking;
        if (source is null || target is null)
            return;
        if (source.GlobalId == target.GlobalId)
            return;
        if (source.IsVirtualMember || target.IsVirtualMember)
            throw new InvalidOperationException("Projected relation members are read-only.");

        if (IsInstanceKind(source.SemanticKind) && target.SemanticKind == SemanticElementKind.Type) {
            await AddDeclaredTypeAsync(source.GlobalId, target.GlobalId).ConfigureAwait(false);
            return;
        }
        if (IsInstanceKind(target.SemanticKind) && source.SemanticKind == SemanticElementKind.Type) {
            await AddDeclaredTypeAsync(target.GlobalId, source.GlobalId).ConfigureAwait(false);
            return;
        }
        if (!IsInstanceKind(source.SemanticKind) || !IsInstanceKind(target.SemanticKind))
            throw new InvalidOperationException("Untyped relations can connect only instance-graph elements.");
        var existing = await FindUntypedRelationsBetweenAsync(source.GlobalId, target.GlobalId).ConfigureAwait(false);
        if (existing.Count > 0)
            throw new InvalidOperationException($"Nodes '{source.GlobalId}' and '{target.GlobalId}' are already connected.");

        var relationId = await NextConnectionIdAsync().ConfigureAwait(false);
        var record = new SemanticInstanceFileRecord {
            Id = FormatId(relationId),
            Kind = "relation",
            Members = [
                new SemanticRelationMemberFileRecord {
                    Name = "Node1",
                    MemberTypeId = string.Empty,
                    ParticipantIds = [FormatId(source.GlobalId)]
                },
                new SemanticRelationMemberFileRecord {
                    Name = "Node2",
                    MemberTypeId = string.Empty,
                    ParticipantIds = [FormatId(target.GlobalId)]
                }
            ]
        };
        var selfMarkerCreated = await EnsureIncidentMarkerAsync(relationId).ConfigureAwait(false);
        var indexMayContainEntries = false;
        try {
            indexMayContainEntries = true;
            await AddRelationToIncidenceIndexAsync(record).ConfigureAwait(false);
            await CreateInstanceRecordAsync(relationId, record).ConfigureAwait(false);
        } catch {
            if (indexMayContainEntries)
                await RemoveRelationFromIncidenceIndexAsync(record).ConfigureAwait(false);
            if (selfMarkerCreated)
                await DeleteIncidentMarkerAsync(relationId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task Disconnect(NodeRef sourcePath, NodeRef targetPath)
    {
        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            await DisconnectCoreAsync(sourcePath, targetPath).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task DisconnectCoreAsync(NodeRef sourcePath, NodeRef targetPath)
    {
        var source = await Get(sourcePath).ConfigureAwait(false) as SemanticFileNodeBacking;
        var target = await Get(targetPath).ConfigureAwait(false) as SemanticFileNodeBacking;
        if (source is null || target is null || source.GlobalId == target.GlobalId)
            return;

        if (IsInstanceKind(source.SemanticKind) && target.SemanticKind == SemanticElementKind.Type) {
            await RemoveDeclaredTypeAsync(source.GlobalId, target.GlobalId).ConfigureAwait(false);
            return;
        }
        if (IsInstanceKind(target.SemanticKind) && source.SemanticKind == SemanticElementKind.Type) {
            await RemoveDeclaredTypeAsync(target.GlobalId, source.GlobalId).ConfigureAwait(false);
            return;
        }
        var relations = await FindUntypedRelationsBetweenAsync(source.GlobalId, target.GlobalId).ConfigureAwait(false);
        if (relations.Count > 1)
            throw new InvalidOperationException("More than one untyped relation connects the selected instances.");
        if (relations.Count == 0)
            return;
        await DeleteInstanceClosureAsync(relations.Single()).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<NodeBacking> GetNeighbors(NodeBacking node)
    {
        if (node is not SemanticFileNodeBacking semantic || !ReferenceEquals(semantic.Storage, this))
            yield break;
        await foreach (var neighbor in GetProjectedNeighborsAsync(semantic).ConfigureAwait(false))
            yield return neighbor;
    }

    public async IAsyncEnumerable<NodeBacking> GetNeighbors(NodeRef path)
    {
        var node = await Get(path).ConfigureAwait(false);
        if (node is null)
            yield break;
        await foreach (var neighbor in GetNeighbors(node).ConfigureAwait(false))
            yield return neighbor;
    }

    public async IAsyncEnumerable<NodeBacking> GetCommonIntersection(
        NodeRef first,
        NodeRef second,
        params NodeRef[] other)
    {
        var nodes = new List<NodeBacking>();
        foreach (var id in new[] { first, second }.Concat(other)) {
            var node = await Get(id).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Node '{id}' was not found.");
            nodes.Add(node);
        }

        var candidates = await GetNeighbors(nodes[0]).ToArrayAsync().ConfigureAwait(false);
        foreach (var candidate in candidates) {
            var presentEverywhere = true;
            foreach (var node in nodes.Skip(1)) {
                if (!await GetNeighbors(node).AnyAsync(value => value.GlobalId == candidate.GlobalId).ConfigureAwait(false)) {
                    presentEverywhere = false;
                    break;
                }
            }
            if (presentEverywhere)
                yield return candidate;
        }
    }

    public async IAsyncEnumerable<NodeBacking> EnumerateNodesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return TypeRoot;
        await foreach (var (id, record) in EnumerateTypeRecordsAsync(cancellationToken).ConfigureAwait(false)) {
            yield return new SemanticFileNodeBacking(
                this,
                id,
                ParseTypeKind(record, id));
        }

        await foreach (var (id, record) in EnumerateInstanceRecordsAsync(cancellationToken).ConfigureAwait(false)) {
            if (IsUntypedBinaryRelation(record))
                continue;
            var kind = ParseInstanceKind(record, id);
            yield return new SemanticFileNodeBacking(this, id, kind);
            if (kind == SemanticElementKind.Relation)
                for (var index = 0; index < record.Members.Count; index++)
                    yield return CreateMemberBacking(id, record.Members[index], index);
        }
    }

    public async IAsyncEnumerable<NodeBacking> EnumerateSemanticElementsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return TypeRoot;
        await foreach (var (id, record) in EnumerateTypeRecordsAsync(cancellationToken).ConfigureAwait(false)) {
            yield return new SemanticFileNodeBacking(
                this,
                id,
                ParseTypeKind(record, id));
        }

        await foreach (var (id, record) in EnumerateInstanceRecordsAsync(cancellationToken).ConfigureAwait(false))
            yield return new SemanticFileNodeBacking(
                this,
                id,
                ParseInstanceKind(record, id));
    }

    public async IAsyncEnumerable<NodeBacking> EnumerateTypeElementsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return TypeRoot;
        await foreach (var (id, record) in EnumerateTypeRecordsAsync(cancellationToken).ConfigureAwait(false)) {
            yield return new SemanticFileNodeBacking(
                this,
                id,
                ParseTypeKind(record, id));
        }
    }

    public async IAsyncEnumerable<NodeBacking> EnumerateInstanceNamespaceChildrenAsync(
        NodeRef? parent = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parentId = parent is null ? new InternalId() : ToInternalId(parent);
        if (IsTypeAddress(parentId))
            throw new InvalidOperationException("Instance namespace cannot be enumerated below the type catalog.");

        var directory = IsRoot(parentId) ? instancesPath : GetInstanceDirectory(parentId);
        if (!Directory.Exists(directory))
            yield break;

        await foreach (var child in EnumerateNearestInstanceRecordsAsync(directory, cancellationToken)
                           .ConfigureAwait(false))
            yield return child;
    }

    internal async IAsyncEnumerable<NodeBacking> GetProjectedNeighborsAsync(
        SemanticFileNodeBacking owner,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<InternalId, NodeBacking>();
        void Add(NodeBacking node)
        {
            if (node.GlobalId != owner.GlobalId)
                result[node.GlobalId] = node;
        }

        switch (owner.SemanticKind) {
            case SemanticElementKind.Root:
                Add(TypeRoot);
                break;

            case SemanticElementKind.TypeCatalog:
                foreach (var child in await GetTypeAddressChildrenAsync(owner.GlobalId, cancellationToken).ConfigureAwait(false))
                    Add(child);
                break;

            case SemanticElementKind.Type:
            case SemanticElementKind.TypeInfrastructure:
                var typeParent = await GetTypeAddressParentAsync(owner.GlobalId).ConfigureAwait(false);
                if (typeParent is not null)
                    Add(typeParent);
                foreach (var child in await GetTypeAddressChildrenAsync(owner.GlobalId, cancellationToken).ConfigureAwait(false))
                    Add(child);
                var typeRecord = await ReadTypeRecordAsync(owner.GlobalId, cancellationToken).ConfigureAwait(false);
                foreach (var requiredId in typeRecord?.RequiredTypeIds ?? Array.Empty<string>())
                    Add(CreateTypeReferenceBacking(ParseId(requiredId)));
                break;

            case SemanticElementKind.Instance:
                foreach (var typeId in await ReadDeclaredTypeIdsAsync(owner.GlobalId, cancellationToken).ConfigureAwait(false))
                    Add(CreateTypeReferenceBacking(typeId));
                await AddIncidentProjectionAsync(owner.GlobalId, Add, cancellationToken).ConfigureAwait(false);
                break;

            case SemanticElementKind.Relation:
                var relationRecord = await ReadInstanceRecordAsync(owner.GlobalId, cancellationToken).ConfigureAwait(false);
                if (relationRecord is null)
                    break;
                if (IsUntypedBinaryRelation(relationRecord)) {
                    foreach (var participant in relationRecord.Members.SelectMany(static member => member.ParticipantIds))
                        if (await Get(ParseId(participant)).ConfigureAwait(false) is { } participantBacking)
                            Add(participantBacking);
                    break;
                }
                foreach (var typeId in relationRecord.TypeIds)
                    Add(CreateTypeReferenceBacking(ParseId(typeId)));
                for (var index = 0; index < relationRecord.Members.Count; index++)
                    Add(CreateMemberBacking(owner.GlobalId, relationRecord.Members[index], index));
                break;

            case SemanticElementKind.RelationMember:
                var member = await ReadVirtualMemberAsync(owner, cancellationToken).ConfigureAwait(false);
                if (member is null)
                    break;
                if (await Get(member.Value.RelationId).ConfigureAwait(false) is { } relation)
                    Add(relation);
                if (!string.IsNullOrWhiteSpace(member.Value.Record.MemberTypeId))
                    Add(CreateTypeReferenceBacking(ParseId(member.Value.Record.MemberTypeId)));
                foreach (var participantId in member.Value.Record.ParticipantIds)
                    if (await Get(ParseId(participantId)).ConfigureAwait(false) is { } participant)
                        Add(participant);
                break;
        }

        foreach (var neighbor in result.Values.OrderBy(static node => node.GlobalId.ToString(), StringComparer.Ordinal))
            yield return neighbor;
    }

    internal IReadOnlyCollection<InternalId> ReadDeclaredTypeIds(InternalId id) =>
        ReadDeclaredTypeIdsAsync(id).GetAwaiter().GetResult();

    internal IReadOnlyCollection<SemanticRelationMember> ReadRelationMembers(SemanticFileNodeBacking backing)
    {
        if (backing.SemanticKind != SemanticElementKind.RelationMember)
            return ReadRelationMembersAsync(backing.GlobalId).GetAwaiter().GetResult();
        if (backing.MemberIndex is not { } memberIndex)
            return Array.Empty<SemanticRelationMember>();

        var segments = backing.GlobalId.ToArray();
        if (segments.Length < 2)
            return Array.Empty<SemanticRelationMember>();
        var relationId = new InternalId(segments.Take(segments.Length - 1));
        var relation = ReadInstanceRecordAsync(relationId).GetAwaiter().GetResult();
        return relation is { Kind: "relation" }
               && memberIndex >= 0
               && memberIndex < relation.Members.Count
            ? [ToSemanticMember(relation.Members[memberIndex])]
            : Array.Empty<SemanticRelationMember>();
    }

    internal IDictionary<string, string> ReadAttributes(InternalId id, SemanticElementKind kind)
    {
        if (kind is SemanticElementKind.Root or SemanticElementKind.TypeCatalog or SemanticElementKind.RelationMember)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (kind is SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure) {
            var record = ReadTypeRecordAsync(id).GetAwaiter().GetResult();
            return new Dictionary<string, string>(
                record?.Attributes ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        var instance = ReadInstanceRecordAsync(id).GetAwaiter().GetResult();
        return new Dictionary<string, string>(
            instance?.Attributes ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    internal void ReplaceAttributes(InternalId id, SemanticElementKind kind, IDictionary<string, string> attributes) =>
        ReplaceAttributesAsync(id, attributes, kind).GetAwaiter().GetResult();

    internal async Task<NodeBacking> AddProjectedNodeAsync(SemanticFileNodeBacking owner, NodeBacking item)
    {
        if (item is VirtualNodeState) {
            var created = await Create(item.LocalId, owner.GlobalId, item.Attributes).ConfigureAwait(false);
            if (IsInstanceKind(owner.SemanticKind)
                && created is ISemanticNodeBacking { SemanticKind: SemanticElementKind.Instance or SemanticElementKind.Relation })
                await Connect(owner.GlobalId, created.GlobalId).ConfigureAwait(false);
            return created;
        }
        await Connect(owner.GlobalId, item.GlobalId).ConfigureAwait(false);
        return item;
    }

    internal Task RemoveProjectedNodeAsync(SemanticFileNodeBacking owner, NodeBacking item) =>
        Disconnect(owner.GlobalId, item.GlobalId);

    internal async Task ClearProjectedNodesAsync(SemanticFileNodeBacking owner)
    {
        var neighbors = await GetProjectedNeighborsAsync(owner).ToArrayAsync().ConfigureAwait(false);
        foreach (var neighbor in neighbors)
            await Disconnect(owner.GlobalId, neighbor.GlobalId).ConfigureAwait(false);
    }

    public async Task RepairDerivedIndexesAsync(CancellationToken cancellationToken = default)
    {
        await instanceMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await RepairDerivedIndexesCoreAsync(cancellationToken).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task RepairDerivedIndexesCoreAsync(CancellationToken cancellationToken)
    {
        // Full enumeration is an explicit repair operation. Normal opening,
        // reads, and relation mutations never scan unrelated instance records.
        await incidenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var entriesByParticipant = new Dictionary<string, List<SemanticIncidentIndexEntryFileRecord>>(StringComparer.Ordinal);
            var canonicalIds = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var (_, record) in EnumerateInstanceRecordsAsync(cancellationToken).ConfigureAwait(false)) {
                canonicalIds.Add(record.Id);
                entriesByParticipant.TryAdd(record.Id, []);
                if (!string.Equals(record.Kind, "relation", StringComparison.OrdinalIgnoreCase))
                    continue;
                for (var memberIndex = 0; memberIndex < record.Members.Count; memberIndex++) {
                    var member = record.Members[memberIndex];
                    foreach (var participantId in member.ParticipantIds.Distinct(StringComparer.Ordinal)) {
                        if (!entriesByParticipant.TryGetValue(participantId, out var entries)) {
                            entries = [];
                            entriesByParticipant[participantId] = entries;
                        }
                        entries.Add(new SemanticIncidentIndexEntryFileRecord {
                            RelationId = record.Id,
                            MemberName = member.Name,
                            MemberIndex = memberIndex
                        });
                    }
                }
            }

            var missingParticipants = entriesByParticipant.Keys
                .Where(participantId => !canonicalIds.Contains(participantId))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missingParticipants.Length > 0)
                throw new InvalidDataException(
                    $"Cannot repair incidence indexes because canonical relations reference missing participants: {string.Join(", ", missingParticipants)}.");

            if (Directory.Exists(incidentsPath))
                Directory.Delete(incidentsPath, recursive: true);
            Directory.CreateDirectory(incidentsPath);

            foreach (var (participantId, entries) in entriesByParticipant.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
                var record = new SemanticIncidentIndexFileRecord {
                    ParticipantId = participantId,
                    Relations = entries
                        .DistinctBy(static entry => (entry.RelationId, entry.MemberIndex, entry.MemberName))
                        .OrderBy(static entry => entry.RelationId, StringComparer.Ordinal)
                        .ThenBy(static entry => entry.MemberIndex)
                        .ThenBy(static entry => entry.MemberName, StringComparer.Ordinal)
                        .ToArray()
                };
                await AtomicJsonFile.WriteAsync(
                    GetIncidentRecordPath(ParseId(participantId)),
                    record,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task AddRelationToIncidenceIndexAsync(
        SemanticInstanceFileRecord relation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(relation.Kind, "relation", StringComparison.OrdinalIgnoreCase))
            return;

        var entriesByParticipant = CreateIncidentEntries(relation);
        await incidenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var existingByParticipant = new Dictionary<string, SemanticIncidentIndexFileRecord>(StringComparer.Ordinal);
            foreach (var participantId in entriesByParticipant.Keys) {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                    GetIncidentRecordPath(ParseId(participantId)),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null
                    || !string.Equals(existing.ParticipantId, participantId, StringComparison.Ordinal))
                    throw IndexRepairRequired(participantId);
                existingByParticipant[participantId] = existing;
            }

            foreach (var (participantId, newEntries) in entriesByParticipant) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = GetIncidentRecordPath(ParseId(participantId));
                var existing = existingByParticipant[participantId];
                var entries = existing.Relations
                    .Concat(newEntries)
                    .DistinctBy(static entry => (entry.RelationId, entry.MemberIndex, entry.MemberName))
                    .OrderBy(static entry => entry.RelationId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.MemberIndex)
                    .ThenBy(static entry => entry.MemberName, StringComparer.Ordinal)
                    .ToArray();
                await AtomicJsonFile.WriteAsync(
                    path,
                    new SemanticIncidentIndexFileRecord {
                        ParticipantId = participantId,
                        Relations = entries
                    },
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task RemoveRelationFromIncidenceIndexAsync(
        SemanticInstanceFileRecord relation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(relation.Kind, "relation", StringComparison.OrdinalIgnoreCase))
            return;

        var relationId = relation.Id;
        var participantIds = relation.Members
            .SelectMany(static member => member.ParticipantIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await incidenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            foreach (var participantId in participantIds) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = GetIncidentRecordPath(ParseId(participantId));
                var existing = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                    path,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                    continue;

                var entries = existing.Relations
                    .Where(entry => !string.Equals(entry.RelationId, relationId, StringComparison.Ordinal))
                    .ToArray();
                await AtomicJsonFile.WriteAsync(
                    path,
                    existing with { Relations = entries },
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task ReconcileRelationIncidenceIndexAsync(
        SemanticInstanceFileRecord desired,
        SemanticInstanceFileRecord previous)
    {
        var desiredByParticipant = CreateIncidentEntries(desired);
        var affectedParticipantIds = desired.Members
            .SelectMany(static member => member.ParticipantIds)
            .Concat(previous.Members.SelectMany(static member => member.ParticipantIds))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await incidenceGate.WaitAsync().ConfigureAwait(false);
        try {
            foreach (var participantId in affectedParticipantIds) {
                var path = GetIncidentRecordPath(ParseId(participantId));
                var existing = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                    path,
                    SerializerOptions).ConfigureAwait(false);
                desiredByParticipant.TryGetValue(participantId, out var desiredEntries);
                desiredEntries ??= Array.Empty<SemanticIncidentIndexEntryFileRecord>();
                if (existing is null) {
                    if (desiredEntries.Count > 0)
                        throw IndexRepairRequired(participantId);
                    continue;
                }
                if (!string.Equals(existing.ParticipantId, participantId, StringComparison.Ordinal))
                    throw IndexRepairRequired(participantId);

                var entries = existing.Relations
                    .Where(entry => !string.Equals(entry.RelationId, desired.Id, StringComparison.Ordinal))
                    .Concat(desiredEntries)
                    .DistinctBy(static entry => (entry.RelationId, entry.MemberIndex, entry.MemberName))
                    .OrderBy(static entry => entry.RelationId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.MemberIndex)
                    .ThenBy(static entry => entry.MemberName, StringComparer.Ordinal)
                    .ToArray();
                await AtomicJsonFile.WriteAsync(
                    path,
                    existing with { Relations = entries },
                    SerializerOptions).ConfigureAwait(false);
            }
        } finally {
            incidenceGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<SemanticIncidentIndexEntryFileRecord>>
        CreateIncidentEntries(SemanticInstanceFileRecord relation)
    {
        var result = new Dictionary<string, List<SemanticIncidentIndexEntryFileRecord>>(StringComparer.Ordinal);
        for (var memberIndex = 0; memberIndex < relation.Members.Count; memberIndex++) {
            var member = relation.Members[memberIndex];
            foreach (var participantId in member.ParticipantIds.Distinct(StringComparer.Ordinal)) {
                if (!result.TryGetValue(participantId, out var entries)) {
                    entries = [];
                    result[participantId] = entries;
                }
                entries.Add(new SemanticIncidentIndexEntryFileRecord {
                    RelationId = relation.Id,
                    MemberName = member.Name,
                    MemberIndex = memberIndex
                });
            }
        }
        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyCollection<SemanticIncidentIndexEntryFileRecord>)pair.Value,
            StringComparer.Ordinal);
    }

    private async Task AddIncidentProjectionAsync(
        InternalId participantId,
        Action<NodeBacking> add,
        CancellationToken cancellationToken)
    {
        var entries = await ReadIncidentEntriesAsync(participantId, cancellationToken).ConfigureAwait(false);
        var participantText = FormatId(participantId);
        foreach (var entry in entries) {
            var relationId = ParseId(entry.RelationId);
            var relation = await ReadInstanceRecordAsync(relationId, cancellationToken).ConfigureAwait(false);
            if (relation is not { Kind: "relation" })
                continue;
            if (entry.MemberIndex < 0 || entry.MemberIndex >= relation.Members.Count)
                continue;
            var indexedMember = relation.Members[entry.MemberIndex];
            if (!string.Equals(indexedMember.Name, entry.MemberName, StringComparison.Ordinal)
                || !indexedMember.ParticipantIds.Contains(participantText, StringComparer.Ordinal))
                continue;
            if (IsUntypedBinaryRelation(relation)) {
                foreach (var otherId in relation.Members
                             .SelectMany(static member => member.ParticipantIds)
                             .Where(id => !string.Equals(id, FormatId(participantId), StringComparison.Ordinal))
                             .Distinct(StringComparer.Ordinal))
                    if (await Get(ParseId(otherId)).ConfigureAwait(false) is { } other)
                        add(other);
                continue;
            }
            add(CreateMemberBacking(relationId, indexedMember, entry.MemberIndex));
        }
    }

    private async Task<IReadOnlyCollection<SemanticIncidentIndexEntryFileRecord>> ReadIncidentEntriesAsync(
        InternalId participantId,
        CancellationToken cancellationToken)
    {
        await incidenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var record = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                GetIncidentRecordPath(participantId),
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return record?.Relations.ToArray() ?? Array.Empty<SemanticIncidentIndexEntryFileRecord>();
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task<IReadOnlyCollection<SemanticIncidentIndexEntryFileRecord>> ReadRequiredIncidentEntriesAsync(
        InternalId participantId,
        CancellationToken cancellationToken)
    {
        var participantText = FormatId(participantId);
        await incidenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var record = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                GetIncidentRecordPath(participantId),
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (record is null
                || !string.Equals(record.ParticipantId, participantText, StringComparison.Ordinal))
                throw IndexRepairRequired(participantText);
            return record.Relations.ToArray();
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task<bool> EnsureIncidentMarkerAsync(InternalId participantId)
    {
        var participantText = FormatId(participantId);
        await incidenceGate.WaitAsync().ConfigureAwait(false);
        try {
            var path = GetIncidentRecordPath(participantId);
            var existing = await AtomicJsonFile.ReadAsync<SemanticIncidentIndexFileRecord>(
                path,
                SerializerOptions).ConfigureAwait(false);
            if (existing is not null) {
                if (!string.Equals(existing.ParticipantId, participantText, StringComparison.Ordinal))
                    throw IndexRepairRequired(participantText);
                return false;
            }
            await AtomicJsonFile.WriteAsync(
                path,
                new SemanticIncidentIndexFileRecord {
                    ParticipantId = participantText
                },
                SerializerOptions).ConfigureAwait(false);
            return true;
        } finally {
            incidenceGate.Release();
        }
    }

    private async Task DeleteIncidentMarkerAsync(InternalId participantId)
    {
        await incidenceGate.WaitAsync().ConfigureAwait(false);
        try {
            DeleteFileAndPruneEmptyDirectories(GetIncidentRecordPath(participantId), incidentsPath);
        } finally {
            incidenceGate.Release();
        }
    }

    private static InvalidOperationException IndexRepairRequired(string participantId) =>
        new(
            $"Incidence index marker for '{participantId}' is missing or invalid. "
            + "Run RepairDerivedIndexesAsync before mutating the instance graph.");

    private async Task AddDeclaredTypeAsync(InternalId instanceId, InternalId typeId)
    {
        await MutateInstanceRecordAsync(instanceId, record => record with {
            TypeIds = record.TypeIds
                .Append(FormatId(typeId))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        }).ConfigureAwait(false);
    }

    private async Task RemoveDeclaredTypeAsync(InternalId instanceId, InternalId typeId)
    {
        var typeText = FormatId(typeId);
        await MutateInstanceRecordAsync(instanceId, record => record with {
            TypeIds = record.TypeIds.Where(id => !string.Equals(id, typeText, StringComparison.Ordinal)).ToArray()
        }).ConfigureAwait(false);
    }

    private async Task DeleteCoreAsync(SemanticFileNodeBacking node)
    {
        if (node.SemanticKind is SemanticElementKind.Root or SemanticElementKind.TypeCatalog)
            throw new InvalidOperationException("Storage roots cannot be deleted.");
        if (node.IsVirtualMember)
            throw new InvalidOperationException("Projected relation members are read-only.");

        if (node.SemanticKind is SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure) {
            await typeMutationGate.WaitAsync().ConfigureAwait(false);
            try {
                var directory = GetTypeDirectory(node.GlobalId);
                DeleteDirectoryWithin(directory, typesPath);
            } finally {
                typeMutationGate.Release();
            }
            return;
        }

        await instanceMutationGate.WaitAsync().ConfigureAwait(false);
        try {
            await DeleteInstanceClosureAsync(node.GlobalId).ConfigureAwait(false);
        } finally {
            instanceMutationGate.Release();
        }
    }

    private async Task DeleteInstanceClosureAsync(InternalId rootId)
    {
        var records = new Dictionary<string, (InternalId Id, SemanticInstanceFileRecord Record)>(StringComparer.Ordinal);
        var outgoing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var indegrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<InternalId>();
        pending.Enqueue(rootId);
        queued.Add(FormatId(rootId));

        while (pending.TryDequeue(out var currentId)) {
            var current = await ReadInstanceRecordAsync(currentId).ConfigureAwait(false);
            if (current is null)
                continue;

            var currentKey = FormatId(currentId);
            records[currentKey] = (currentId, current);
            indegrees.TryAdd(currentKey, 0);

            var incidentEntries = await ReadRequiredIncidentEntriesAsync(currentId, CancellationToken.None).ConfigureAwait(false);
            foreach (var relationText in incidentEntries
                         .Select(static entry => entry.RelationId)
                         .Distinct(StringComparer.Ordinal)) {
                var relationId = ParseId(relationText);
                var relation = await ReadInstanceRecordAsync(relationId).ConfigureAwait(false);
                if (relation is not { Kind: "relation" }
                    || !RelationReferencesParticipant(relation, currentKey))
                    continue; // stale derived entry; never cascade from it

                var relationKey = FormatId(relationId);
                indegrees.TryAdd(relationKey, 0);
                if (!outgoing.TryGetValue(relationKey, out var targets)) {
                    targets = new HashSet<string>(StringComparer.Ordinal);
                    outgoing[relationKey] = targets;
                }
                if (targets.Add(currentKey))
                    indegrees[currentKey]++;
                if (queued.Add(relationKey))
                    pending.Enqueue(relationId);
            }
        }

        var ready = new SortedSet<string>(
            indegrees.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            StringComparer.Ordinal);
        var deletionOrder = new List<string>(records.Count);
        while (ready.Count > 0) {
            var currentKey = ready.Min!;
            ready.Remove(currentKey);
            deletionOrder.Add(currentKey);
            if (!outgoing.TryGetValue(currentKey, out var targets))
                continue;
            foreach (var target in targets) {
                indegrees[target]--;
                if (indegrees[target] == 0)
                    ready.Add(target);
            }
        }

        if (deletionOrder.Count != records.Count)
            throw new InvalidOperationException(
                $"Cannot delete '{rootId}' because its higher-order relation closure contains a cycle.");

        foreach (var key in deletionOrder) {
            var (currentId, current) = records[key];
            // Relations that reference this element were deleted earlier in
            // the topological order. A crash cannot leave a surviving
            // canonical relation pointing at an already deleted participant.
            await DeleteExactInstanceRecordAsync(currentId).ConfigureAwait(false);
            if (string.Equals(current.Kind, "relation", StringComparison.OrdinalIgnoreCase))
                await RemoveRelationFromIncidenceIndexAsync(current).ConfigureAwait(false);
            await DeleteIncidentMarkerAsync(currentId).ConfigureAwait(false);
        }
    }

    private async Task ReplaceAttributesAsync(
        InternalId id,
        IDictionary<string, string> attributes,
        SemanticElementKind? knownKind = null)
    {
        var kind = knownKind ?? (await Get(id).ConfigureAwait(false) as ISemanticNodeBacking)?.SemanticKind;
        if (kind is SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure) {
            await typeMutationGate.WaitAsync().ConfigureAwait(false);
            try {
                await MutateTypeRecordAsync(id, record => record with { Attributes = CopyAttributes(attributes) })
                    .ConfigureAwait(false);
            } finally {
                typeMutationGate.Release();
            }
            return;
        }
        if (kind is SemanticElementKind.Instance or SemanticElementKind.Relation) {
            await instanceMutationGate.WaitAsync().ConfigureAwait(false);
            try {
                await MutateInstanceRecordAsync(id, record => record with { Attributes = CopyAttributes(attributes) })
                    .ConfigureAwait(false);
            } finally {
                instanceMutationGate.Release();
            }
            return;
        }
        if (kind is SemanticElementKind.Root or SemanticElementKind.TypeCatalog or SemanticElementKind.RelationMember)
            throw new InvalidOperationException("Projected storage nodes do not have persisted attributes.");
    }

    private async Task<IReadOnlyCollection<InternalId>> ReadDeclaredTypeIdsAsync(
        InternalId id,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadInstanceRecordAsync(id, cancellationToken).ConfigureAwait(false);
        return record?.TypeIds.Select(ParseId).ToArray() ?? Array.Empty<InternalId>();
    }

    private async Task<IReadOnlyCollection<SemanticRelationMember>> ReadRelationMembersAsync(
        InternalId id,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadInstanceRecordAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is not { Kind: "relation" })
            return Array.Empty<SemanticRelationMember>();
        return record.Members.Select(ToSemanticMember).ToArray();
    }

    private async Task<SemanticTypeFileRecord?> ReadTypeRecordAsync(
        InternalId id,
        CancellationToken cancellationToken = default)
    {
        if (!IsTypeAddress(id) || IsTypeRoot(id))
            return null;
        var record = await AtomicJsonFile.ReadAsync<SemanticTypeFileRecord>(
            Path.Combine(GetTypeDirectory(id), TypeRecordFileName),
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (!CanonicalRecordIdMatches(id, record?.Id, "type"))
            return null;
        if (record is not null)
            _ = ParseTypeKind(record, id);
        return record;
    }

    private async Task<SemanticInstanceFileRecord?> ReadInstanceRecordAsync(
        InternalId id,
        CancellationToken cancellationToken = default)
    {
        if (IsRoot(id) || IsTypeAddress(id))
            return null;
        var record = await AtomicJsonFile.ReadAsync<SemanticInstanceFileRecord>(
            Path.Combine(GetInstanceDirectory(id), InstanceRecordFileName),
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (!CanonicalRecordIdMatches(id, record?.Id, "instance"))
            return null;
        if (record is not null)
            _ = ParseInstanceKind(record, id);
        return record;
    }

    private async Task CreateTypeRecordAsync(InternalId id, SemanticTypeFileRecord record)
    {
        var path = Path.Combine(GetTypeDirectory(id), TypeRecordFileName);
        await CreateRecordAsync(path, record).ConfigureAwait(false);
    }

    private async Task CreateInstanceRecordAsync(InternalId id, SemanticInstanceFileRecord record)
    {
        var path = Path.Combine(GetInstanceDirectory(id), InstanceRecordFileName);
        await CreateRecordAsync(path, record).ConfigureAwait(false);
    }

    private async Task CreateRecordAsync<T>(string path, T record)
    {
        var gate = GetFileGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            if (File.Exists(path))
                throw new InvalidOperationException($"Semantic record '{path}' already exists.");
            await AtomicJsonFile.WriteAsync(path, record, SerializerOptions).ConfigureAwait(false);
        } finally {
            gate.Release();
        }
    }

    private async Task MutateTypeRecordAsync(
        InternalId id,
        Func<SemanticTypeFileRecord, SemanticTypeFileRecord> mutation)
    {
        var path = Path.Combine(GetTypeDirectory(id), TypeRecordFileName);
        var gate = GetFileGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            var record = await AtomicJsonFile.ReadAsync<SemanticTypeFileRecord>(path, SerializerOptions).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Type '{id}' was not found.");
            await AtomicJsonFile.WriteAsync(path, mutation(record), SerializerOptions).ConfigureAwait(false);
        } finally {
            gate.Release();
        }
    }

    private async Task MutateInstanceRecordAsync(
        InternalId id,
        Func<SemanticInstanceFileRecord, SemanticInstanceFileRecord> mutation)
    {
        var path = Path.Combine(GetInstanceDirectory(id), InstanceRecordFileName);
        var gate = GetFileGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            var record = await AtomicJsonFile.ReadAsync<SemanticInstanceFileRecord>(path, SerializerOptions).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Instance '{id}' was not found.");
            await AtomicJsonFile.WriteAsync(path, mutation(record), SerializerOptions).ConfigureAwait(false);
        } finally {
            gate.Release();
        }
    }

    private async Task DeleteExactInstanceRecordAsync(InternalId id)
    {
        var path = Path.Combine(GetInstanceDirectory(id), InstanceRecordFileName);
        var gate = GetFileGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            DeleteFileAndPruneEmptyDirectories(path, instancesPath);
        } finally {
            gate.Release();
        }
    }

    private async Task<IReadOnlyCollection<InternalId>> FindUntypedRelationsBetweenAsync(
        InternalId first,
        InternalId second)
    {
        var expected = new[] { FormatId(first), FormatId(second) }.Order(StringComparer.Ordinal).ToArray();
        var result = new List<InternalId>();
        var entries = await ReadIncidentEntriesAsync(first, CancellationToken.None).ConfigureAwait(false);
        foreach (var relationText in entries.Select(static entry => entry.RelationId).Distinct(StringComparer.Ordinal)) {
            var relationId = ParseId(relationText);
            var record = await ReadInstanceRecordAsync(relationId).ConfigureAwait(false);
            if (!IsUntypedBinaryRelation(record))
                continue;
            var actual = record!.Members
                .SelectMany(static member => member.ParticipantIds)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (actual.SequenceEqual(expected, StringComparer.Ordinal))
                result.Add(relationId);
        }
        return result;
    }

    private async Task<InternalId> NextConnectionIdAsync()
    {
        while (true) {
            var candidate = new InternalId(new NodeLocalId($"connection-{Guid.NewGuid():N}"));
            if (await ReadInstanceRecordAsync(candidate).ConfigureAwait(false) is null)
                return candidate;
        }
    }

    private async Task EnsureInstanceNamespaceParentAllowedAsync(InternalId parentId)
    {
        var segments = parentId.ToArray();
        for (var length = 1; length <= segments.Length; length++) {
            var prefix = new InternalId(segments.Take(length));
            if (await TryGetVirtualMemberAsync(prefix).ConfigureAwait(false) is not null)
                throw new InvalidOperationException(
                    $"Relation member projection '{prefix}' cannot own address children.");
        }
    }

    private async Task<bool> IsVirtualMemberLocalIdAsync(InternalId relationId, NodeLocalId childLocalId)
    {
        var relation = await ReadInstanceRecordAsync(relationId).ConfigureAwait(false);
        if (relation is not { Kind: "relation" } || IsUntypedBinaryRelation(relation))
            return false;
        for (var index = 0; index < relation.Members.Count; index++)
            if (CreateMemberLocalId(relationId.Last(), index) == childLocalId)
                return true;
        return false;
    }

    private async Task EnsureVirtualMemberAddressesAvailableAsync(
        InternalId relationId,
        SemanticInstanceFileRecord relation)
    {
        if (IsUntypedBinaryRelation(relation))
            return;
        for (var index = 0; index < relation.Members.Count; index++) {
            var memberId = Append(relationId, CreateMemberLocalId(relationId.Last(), index));
            var memberDirectory = Path.GetFullPath(Path.Combine(
                GetInstanceDirectory(relationId),
                memberId.Last().ToString()));
            EnsureWithin(memberDirectory, instancesPath);
            if (Directory.Exists(memberDirectory)
                && Directory.EnumerateFiles(
                        memberDirectory,
                        InstanceRecordFileName,
                        SearchOption.AllDirectories)
                    .Any())
                throw new InvalidOperationException(
                    $"Relation member projection '{memberId}' conflicts with a canonical instance address subtree.");
        }
    }

    private static bool CanonicalRecordIdMatches(InternalId requestedId, string? storedId, string recordKind)
    {
        if (storedId is null)
            return true;
        var requestedText = FormatId(requestedId);
        if (string.Equals(storedId, requestedText, StringComparison.Ordinal))
            return true;
        // A case-insensitive filesystem may resolve a differently-cased query
        // to this file. Internal ids remain Ordinal, so that alias is simply
        // absent rather than becoming an accidental second spelling.
        if (string.Equals(storedId, requestedText, StringComparison.OrdinalIgnoreCase))
            return false;
        throw new InvalidDataException(
            $"Canonical {recordKind} record id '{storedId}' does not match requested address '{requestedText}'. " +
            "The storage contains a misplaced record.");
    }

    private async Task<NodeBacking?> TryGetVirtualMemberAsync(InternalId id)
    {
        var segments = id.ToArray();
        if (segments.Length < 2 || !segments[^1].ToString().StartsWith(VirtualMemberPrefix, StringComparison.Ordinal))
            return null;
        var relationId = new InternalId(segments.Take(segments.Length - 1));
        var relation = await ReadInstanceRecordAsync(relationId).ConfigureAwait(false);
        if (relation is not { Kind: "relation" } || IsUntypedBinaryRelation(relation))
            return null;
        for (var index = 0; index < relation.Members.Count; index++) {
            var candidate = CreateMemberBacking(relationId, relation.Members[index], index);
            if (candidate.GlobalId == id)
                return candidate;
        }
        return null;
    }

    private async Task<(InternalId RelationId, SemanticRelationMemberFileRecord Record)?> ReadVirtualMemberAsync(
        SemanticFileNodeBacking member,
        CancellationToken cancellationToken)
    {
        if (member.MemberIndex is not { } memberIndex)
            return null;
        var segments = member.GlobalId.ToArray();
        var relationId = new InternalId(segments.Take(segments.Length - 1));
        var relation = await ReadInstanceRecordAsync(relationId, cancellationToken).ConfigureAwait(false);
        if (relation is not { Kind: "relation" } || memberIndex < 0 || memberIndex >= relation.Members.Count)
            return null;
        return (relationId, relation.Members[memberIndex]);
    }

    private SemanticFileNodeBacking CreateMemberBacking(
        InternalId relationId,
        SemanticRelationMemberFileRecord member,
        int index)
    {
        var id = Append(relationId, CreateMemberLocalId(relationId.Last(), index));
        return new SemanticFileNodeBacking(this, id, SemanticElementKind.RelationMember, index);
    }

    private SemanticFileNodeBacking CreateTypeReferenceBacking(InternalId typeId) =>
        new(this, typeId, SemanticElementKind.Type);

    private async Task<NodeBacking?> GetTypeAddressParentAsync(InternalId id)
    {
        var segments = id.ToArray();
        if (segments.Length <= 2)
            return segments.Length == 2 ? TypeRoot : null;
        var parentId = new InternalId(segments.Take(segments.Length - 1));
        return await Get(parentId).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<NodeBacking>> GetTypeAddressChildrenAsync(
        InternalId id,
        CancellationToken cancellationToken)
    {
        var directory = IsTypeRoot(id) ? typesPath : GetTypeDirectory(id);
        if (!Directory.Exists(directory))
            return Array.Empty<NodeBacking>();
        var children = new List<NodeBacking>();
        foreach (var childDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            var recordPath = Path.Combine(childDirectory, TypeRecordFileName);
            if (!File.Exists(recordPath))
                continue;
            var childId = Append(id, new NodeLocalId(Path.GetFileName(childDirectory)));
            var child = await Get(childId).ConfigureAwait(false);
            if (child is not null)
                children.Add(child);
        }
        return children;
    }

    private async IAsyncEnumerable<(InternalId Id, SemanticTypeFileRecord Record)> EnumerateTypeRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(typesPath))
            yield break;
        foreach (var path in Directory.EnumerateFiles(typesPath, TypeRecordFileName, SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationTokens.Token.ThrowIfCancellationRequested();
            var record = await AtomicJsonFile.ReadAsync<SemanticTypeFileRecord>(path, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (record is not null) {
                var id = ParseId(record.Id);
                _ = ParseTypeKind(record, id);
                yield return (id, record);
            }
        }
    }

    private async IAsyncEnumerable<NodeBacking> EnumerateNearestInstanceRecordsAsync(
        string directory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(Directory
            .EnumerateDirectories(directory)
            .OrderDescending(StringComparer.OrdinalIgnoreCase));
        while (pending.TryPop(out var childDirectory)) {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationTokens.Token.ThrowIfCancellationRequested();
            var record = await AtomicJsonFile.ReadAsync<SemanticInstanceFileRecord>(
                    Path.Combine(childDirectory, InstanceRecordFileName),
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (record is not null && !IsUntypedBinaryRelation(record)) {
                var id = ParseId(record.Id);
                yield return new SemanticFileNodeBacking(
                    this,
                    id,
                    ParseInstanceKind(record, id));
                continue;
            }

            // Missing records are transparent namespace prefixes, not graph
            // owners. Descend until the nearest canonical records are found.
            foreach (var descendantDirectory in Directory
                         .EnumerateDirectories(childDirectory)
                         .OrderDescending(StringComparer.OrdinalIgnoreCase))
                pending.Push(descendantDirectory);
        }
    }

    private async IAsyncEnumerable<(InternalId Id, SemanticInstanceFileRecord Record)> EnumerateInstanceRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(instancesPath))
            yield break;
        foreach (var path in Directory.EnumerateFiles(instancesPath, InstanceRecordFileName, SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationTokens.Token.ThrowIfCancellationRequested();
            var record = await AtomicJsonFile.ReadAsync<SemanticInstanceFileRecord>(path, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (record is not null) {
                var id = ParseId(record.Id);
                _ = ParseInstanceKind(record, id);
                yield return (id, record);
            }
        }
    }

    private string GetTypeDirectory(InternalId id)
    {
        var segments = id.ToArray();
        if (segments.Length < 2 || segments[0] != TypeRootLocalId)
            throw new InvalidOperationException($"'{id}' is not an address inside the type catalog.");
        return CombineWithin(typesPath, segments.Skip(1));
    }

    private string GetInstanceDirectory(InternalId id)
    {
        if (IsRoot(id) || IsTypeAddress(id))
            throw new InvalidOperationException($"'{id}' is not an instance address.");
        return CombineWithin(instancesPath, id);
    }

    private string GetIncidentRecordPath(InternalId participantId)
    {
        var directory = participantId.Any()
            ? CombineWithin(incidentsPath, participantId)
            : Path.Combine(incidentsPath, "$root");
        return Path.Combine(directory, IncidentRecordFileName);
    }

    private SemaphoreSlim GetFileGate(string path)
    {
        var hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path)));
        return fileGates[hash % (uint)fileGates.Length];
    }

    private static string CombineWithin(string root, IEnumerable<NodeLocalId> segments)
    {
        var result = root;
        foreach (var segment in segments) {
            ValidateSegment(segment, "Address segment");
            result = Path.Combine(result, segment.ToString());
        }
        var full = Path.GetFullPath(result);
        EnsureWithin(full, root);
        return full;
    }

    private static string ResolveConfiguredChild(string root, string name, string optionName)
    {
        ValidateSegment(new NodeLocalId(name), optionName);
        var result = Path.GetFullPath(Path.Combine(root, name));
        EnsureWithin(result, root);
        return result;
    }

    private static void EnsureWithin(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{candidate}' escapes semantic storage root '{root}'.");
    }

    private static void DeleteDirectoryWithin(string directory, string root)
    {
        EnsureWithin(directory, root);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void DeleteFileAndPruneEmptyDirectories(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureWithin(fullPath, root);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(fullPath);
        while (directory is not null
               && !string.Equals(
                   directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any()) {
            var parent = Directory.GetParent(directory)?.FullName;
            Directory.Delete(directory);
            directory = parent;
        }
    }

    private static SemanticRelation ToSemanticRelation(InternalId id, SemanticInstanceFileRecord record) =>
        new(
            id,
            record.TypeIds.Select(ParseId).ToArray(),
            record.Members.Select(ToSemanticMember).ToArray());

    private static SemanticRelationMember ToSemanticMember(SemanticRelationMemberFileRecord member) =>
        new(
            member.Name,
            ParseId(member.MemberTypeId),
            member.ParticipantIds.Select(ParseId).ToArray());

    private static bool IsUntypedBinaryRelation(SemanticInstanceFileRecord? record) =>
        record is { Kind: "relation", TypeIds.Count: 0, Members.Count: 2 }
        && record.Members.All(static member => member.ParticipantIds.Count == 1);

    private static bool RelationReferencesParticipant(
        SemanticInstanceFileRecord relation,
        string participantId) =>
        relation.Members.Any(member => member.ParticipantIds.Contains(participantId, StringComparer.Ordinal));

    private static bool IsInstanceKind(SemanticElementKind kind) =>
        kind is SemanticElementKind.Instance or SemanticElementKind.Relation;

    private static NodeLocalId CreateMemberLocalId(NodeLocalId relationLocalId, int index) =>
        new($"{VirtualMemberPrefix}{relationLocalId}-{index + 1}");

    private static string RequireMemberName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Relation member name is required.", nameof(name));
        return name.Trim();
    }

    private static IReadOnlyDictionary<string, string> CopyAttributes(IDictionary<string, string>? attributes) =>
        attributes is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase);

    private static string[] NormalizeReferenceIds(IEnumerable<NodeRef> references) =>
        references
            .Select(ToInternalId)
            .Select(FormatId)
            .Where(static id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void EnsureTypeElementKind(SemanticElementKind kind)
    {
        if (kind is not (SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure))
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "A type-tree element must be Type or TypeInfrastructure.");
    }

    private static void EnsureInfrastructureHasNoRequiredTypes(
        SemanticElementKind kind,
        IEnumerable<NodeRef> requiredTypeIds)
    {
        if (kind == SemanticElementKind.TypeInfrastructure
            && NormalizeReferenceIds(requiredTypeIds).Length > 0)
            throw new InvalidOperationException(
                "Type infrastructure elements cannot declare required types.");
    }

    private static SemanticElementKind ParseTypeKind(SemanticTypeFileRecord record, InternalId id)
    {
        if (record.FormatVersion != 1)
            throw new InvalidDataException(
                $"Canonical type record '{id}' has unsupported format version '{record.FormatVersion}'.");
        if (record.Attributes is null || record.RequiredTypeIds is null)
            throw new InvalidDataException($"Canonical type record '{id}' has an incomplete body.");
        return record.Kind switch {
            "type" => SemanticElementKind.Type,
            "infrastructure" => SemanticElementKind.TypeInfrastructure,
            _ => throw new InvalidDataException(
                $"Canonical type record '{id}' has invalid kind '{record.Kind}'.")
        };
    }

    private static SemanticElementKind ParseInstanceKind(SemanticInstanceFileRecord record, InternalId id)
    {
        if (record.FormatVersion != 1)
            throw new InvalidDataException(
                $"Canonical instance record '{id}' has unsupported format version '{record.FormatVersion}'.");
        if (record.Attributes is null || record.TypeIds is null || record.Members is null)
            throw new InvalidDataException($"Canonical instance record '{id}' has an incomplete body.");
        return record.Kind switch {
            "instance" => SemanticElementKind.Instance,
            "relation" => SemanticElementKind.Relation,
            _ => throw new InvalidDataException(
                $"Canonical instance record '{id}' has invalid kind '{record.Kind}'.")
        };
    }

    private static InternalId Append(InternalId parent, NodeLocalId child) =>
        new(parent.Concat([child]));

    private static InternalId ToInternalId(NodeRef nodeRef) => nodeRef switch {
        InternalId id => new InternalId(id),
        NodePath path => new InternalId(path),
        _ => throw new ArgumentOutOfRangeException(nameof(nodeRef), nodeRef, "Unsupported node reference.")
    };

    private static InternalId ParseId(string value)
    {
        if (string.IsNullOrEmpty(value))
            return new InternalId();
        return new InternalId(value
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => new NodeLocalId(segment)));
    }

    private static string FormatId(InternalId id) => id.ToString();

    private static bool IsRoot(InternalId id) => !id.Any();

    private static bool IsTypeRoot(InternalId id)
    {
        var segments = id.ToArray();
        return segments.Length == 1 && segments[0] == TypeRootLocalId;
    }

    private static bool IsTypeAddress(InternalId id)
    {
        var segments = id.ToArray();
        return segments.Length > 0 && segments[0] == TypeRootLocalId;
    }

    private static void ValidateSegment(NodeLocalId segment, string subject)
    {
        if (!NodeNameValidator.TryValidateSegment(segment, subject, out var error))
            throw new ArgumentException(error, subject);
        if (string.Equals(segment, TypeRecordFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, InstanceRecordFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, IncidentRecordFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{subject} '{segment}' is reserved by semantic storage.", subject);
    }
}
