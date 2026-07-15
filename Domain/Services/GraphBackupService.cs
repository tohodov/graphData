using System.Text.Json;
using Abstractions;

namespace GraphData.Core.Services;

/// <summary>
/// Backs up canonical semantic records. The derived simple-graph projection is
/// intentionally not serialized because it can always be rebuilt.
/// </summary>
internal sealed class GraphBackupService
{
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };

    private static readonly StringComparer PhysicalIdComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Graph graph;

    public GraphBackupService(GraphService graphService)
    {
        graph = graphService.Model;
    }

    public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var document = await CaptureConsistentDocumentAsync(cancellationToken).ConfigureAwait(false);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true)) {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        } finally {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<GraphBackupDocument> CaptureConsistentDocumentAsync(
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++) {
            var records = new Dictionary<InternalId, GraphBackupRecord>();
            var elementChangedWhileReading = false;
            await foreach (var backing in graph.SemanticStorage
                               .EnumerateSemanticElementsAsync(cancellationToken)
                               .ConfigureAwait(false)) {
                cancellationToken.ThrowIfCancellationRequested();
                var captured = await graph.SemanticStorage
                    .ReadSemanticElementAsync(backing.GlobalId, cancellationToken)
                    .ConfigureAwait(false);
                if (captured is null) {
                    elementChangedWhileReading = true;
                    continue;
                }
                records[captured.Id] = ToBackupRecord(captured);

                var current = await graph.SemanticStorage
                    .ReadSemanticElementAsync(backing.GlobalId, cancellationToken)
                    .ConfigureAwait(false);
                if (!SemanticSnapshotsEqual(captured, current))
                    elementChangedWhileReading = true;
            }

            var document = new GraphBackupDocument {
                Version = CurrentVersion,
                Records = records.Values
                    .OrderBy(static record => record.Path.Length)
                    .ThenBy(static record => PathText(record.Path), StringComparer.Ordinal)
                    .ToList()
            };
            ValidateDocument(document);
            if (!elementChangedWhileReading && IsClosedSemanticSnapshot(document))
                return document;

            if (attempt < maxAttempts)
                await Task.Yield();
        }

        throw new InvalidOperationException(
            "Could not capture a self-consistent semantic backup after three attempts because the graph kept changing.");
    }

    private bool IsClosedSemanticSnapshot(GraphBackupDocument document)
    {
        var records = document.Records.ToDictionary(
            static record => PathText(record.Path),
            StringComparer.Ordinal);
        foreach (var record in document.Records) {
            var kind = ParseKind(record);
            if (kind is SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure) {
                var parentId = PathText(record.Path[..^1]);
                if (!records.TryGetValue(parentId, out var parent)
                    || ParseKind(parent) is not (SemanticElementKind.TypeCatalog
                        or SemanticElementKind.Type
                        or SemanticElementKind.TypeInfrastructure))
                    return false;
            }

            if (kind != SemanticElementKind.Relation)
                continue;
            foreach (var participant in record.Members.SelectMany(static member => member.ParticipantIds)) {
                if (!records.TryGetValue(PathText(participant), out var participantRecord)
                    || ParseKind(participantRecord) is not (SemanticElementKind.Instance or SemanticElementKind.Relation))
                    return false;
            }
        }

        _ = OrderRelations(document.Records);
        return true;
    }

    public async Task RestoreAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        var document = await JsonSerializer.DeserializeAsync<GraphBackupDocument>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Graph backup file is empty.");

        var restorePlan = await ValidateRestorePlanAsync(document, cancellationToken).ConfigureAwait(false);

        var ordinaryRecords = document.Records
            .Where(static record => ParseKind(record) != SemanticElementKind.Relation)
            .OrderBy(static record => record.Path.Length)
            .ThenBy(static record => PathText(record.Path), StringComparer.Ordinal)
            .ToArray();
        foreach (var record in ordinaryRecords) {
            cancellationToken.ThrowIfCancellationRequested();
            await RestoreOrdinaryRecordAsync(record).ConfigureAwait(false);
        }

        foreach (var relation in restorePlan.Relations) {
            cancellationToken.ThrowIfCancellationRequested();
            await RestoreRelationAsync(relation).ConfigureAwait(false);
        }
    }

    private static GraphBackupRecord ToBackupRecord(SemanticElementSnapshot snapshot) =>
        new() {
            Path = Segments(snapshot.Id),
            Kind = snapshot.Kind.ToString(),
            Attributes = new Dictionary<string, string>(snapshot.Attributes, StringComparer.OrdinalIgnoreCase),
            TypeIds = snapshot.TypeIds.Select(Segments).ToList(),
            RequiredTypeIds = snapshot.RequiredTypeIds.Select(Segments).ToList(),
            Members = snapshot.Members.Select(static member => new GraphBackupMember {
                Name = member.Name,
                MemberTypeId = Segments(member.MemberTypeId),
                ParticipantIds = member.ParticipantIds.Select(Segments).ToList()
            }).ToList()
        };

    private static bool SemanticSnapshotsEqual(
        SemanticElementSnapshot captured,
        SemanticElementSnapshot? current) {
        if (current is null
            || captured.Id != current.Id
            || captured.Kind != current.Kind
            || !captured.Attributes.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(
                    current.Attributes.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase),
                    KeyValuePairComparer.Instance)
            || !captured.TypeIds.SequenceEqual(current.TypeIds)
            || !captured.RequiredTypeIds.SequenceEqual(current.RequiredTypeIds)
            || captured.Members.Count != current.Members.Count)
            return false;

        return captured.Members.Zip(current.Members).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && pair.First.MemberTypeId == pair.Second.MemberTypeId
            && pair.First.ParticipantIds.SequenceEqual(pair.Second.ParticipantIds));
    }

    private async Task RestoreOrdinaryRecordAsync(GraphBackupRecord record)
    {
        var kind = ParseKind(record);
        var path = new NodePath(record.Path);
        var existing = await graph.Storage.Get(path).ConfigureAwait(false);
        switch (kind) {
            case SemanticElementKind.TypeCatalog:
                return;

            case SemanticElementKind.Type:
            case SemanticElementKind.TypeInfrastructure: {
                var requiredTypeIds = record.RequiredTypeIds
                    .Select(static id => (NodeRef)new NodePath(id))
                    .ToArray();
                if (existing is null)
                    await graph.SemanticStorage.CreateTypeElementAsync(
                        new NodeLocalId(record.Path[^1]),
                        new NodePath(record.Path[..^1]),
                        kind,
                        requiredTypeIds,
                        record.Attributes).ConfigureAwait(false);
                else
                    await graph.SemanticStorage.ReplaceTypeElementAsync(
                        existing.GlobalId,
                        kind,
                        requiredTypeIds,
                        record.Attributes).ConfigureAwait(false);
                return;
            }

            case SemanticElementKind.Instance: {
                var typeIds = record.TypeIds
                    .Select(static id => (NodeRef)new NodePath(id))
                    .ToArray();
                if (existing is null)
                    await graph.SemanticStorage.CreateInstanceAsync(
                        new NodeLocalId(record.Path[^1]),
                        record.Path.Length == 1 ? null : new NodePath(record.Path[..^1]),
                        typeIds,
                        record.Attributes).ConfigureAwait(false);
                else
                    await graph.SemanticStorage.ReplaceInstanceAsync(
                        existing.GlobalId,
                        typeIds,
                        record.Attributes).ConfigureAwait(false);
                return;
            }

            default:
                throw new InvalidDataException(
                    $"Backup element '{PathText(record.Path)}' has unsupported ordinary kind '{kind}'.");
        }
    }

    private async Task RestoreRelationAsync(GraphBackupRecord record)
    {
        var desired = PrepareRelation(record);
        var relationPath = new NodePath(record.Path);
        var existing = await graph.Storage.Get(relationPath).ConfigureAwait(false);
        if (existing is null) {
            await CreatePreparedRelationAsync(desired, record.Attributes).ConfigureAwait(false);
            return;
        }

        if (existing is not ISemanticNodeBacking { SemanticKind: SemanticElementKind.Relation })
            throw new InvalidDataException(
                $"Cannot restore relation '{PathText(record.Path)}' over a non-relation element.");

        await graph.SemanticStorage.ReplaceRelationAsync(
            existing.GlobalId,
            desired.TypeIds,
            desired.Members,
            record.Attributes).ConfigureAwait(false);
    }

    private async Task<NodeBacking> CreatePreparedRelationAsync(
        PreparedRelation relation,
        IDictionary<string, string> attributes)
    {
        return await graph.SemanticStorage.CreateRelationAsync(
            relation.LocalId,
            relation.TypeIds,
            relation.Members,
            attributes).ConfigureAwait(false);
    }

    private async Task<RestorePlan> ValidateRestorePlanAsync(
        GraphBackupDocument document,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        var records = document.Records.ToDictionary(
            static record => PathText(record.Path),
            StringComparer.Ordinal);
        var existingExact = new Dictionary<string, SemanticElementKind>(StringComparer.Ordinal);
        var existingPhysical = new Dictionary<string, (string Id, SemanticElementKind Kind)>(PhysicalIdComparer);
        await foreach (var backing in graph.SemanticStorage
                           .EnumerateSemanticElementsAsync(cancellationToken)
                           .ConfigureAwait(false)) {
            if (backing is not ISemanticNodeBacking semantic)
                continue;
            var id = backing.GlobalId.ToString();
            existingExact[id] = semantic.SemanticKind;
            if (existingPhysical.TryGetValue(id, out var collision)
                && !string.Equals(collision.Id, id, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Storage contains physically colliding semantic ids '{collision.Id}' and '{id}'.");
            existingPhysical[id] = (id, semantic.SemanticKind);
        }

        foreach (var record in document.Records) {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedKind = ParseKind(record);
            var recordId = PathText(record.Path);
            if (existingPhysical.TryGetValue(recordId, out var physical)
                && !string.Equals(physical.Id, recordId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Backup element '{recordId}' physically collides with existing semantic id '{physical.Id}'.");
            if (existingExact.TryGetValue(recordId, out var existingKind) && existingKind != expectedKind)
                throw new InvalidDataException(
                    $"Backup element '{recordId}' is a {expectedKind}, but storage already contains a {existingKind} at that id.");

            if (expectedKind is SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure) {
                var parentPath = record.Path[..^1];
                if (records.TryGetValue(PathText(parentPath), out var parentRecord)) {
                    var parentKind = ParseKind(parentRecord);
                    if (parentKind is not (SemanticElementKind.TypeCatalog
                        or SemanticElementKind.Type
                        or SemanticElementKind.TypeInfrastructure))
                        throw new InvalidDataException(
                            $"Type '{PathText(record.Path)}' has non-type parent '{PathText(parentPath)}'.");
                } else {
                    var parent = await graph.Storage.Get(new NodePath(parentPath)).ConfigureAwait(false);
                    if (parent is not ISemanticNodeBacking {
                            SemanticKind: SemanticElementKind.TypeCatalog
                                or SemanticElementKind.Type
                                or SemanticElementKind.TypeInfrastructure
                        })
                        throw new InvalidDataException(
                            $"Type '{PathText(record.Path)}' has missing parent '{PathText(parentPath)}'.");
                }
            }
        }

        foreach (var relation in document.Records.Where(
                     static record => ParseKind(record) == SemanticElementKind.Relation)) {
            foreach (var participantPath in relation.Members
                         .SelectMany(static member => member.ParticipantIds)
                         .Distinct(StringArrayComparer.Instance)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (records.TryGetValue(PathText(participantPath), out var participantRecord)) {
                    if (ParseKind(participantRecord) is not (SemanticElementKind.Instance or SemanticElementKind.Relation))
                        throw new InvalidDataException(
                            $"Relation '{PathText(relation.Path)}' participant '{PathText(participantPath)}' is outside the instance graph.");
                    continue;
                }

                var participant = await graph.Storage.Get(new NodePath(participantPath)).ConfigureAwait(false);
                if (participant is not ISemanticNodeBacking {
                        SemanticKind: SemanticElementKind.Instance or SemanticElementKind.Relation
                    })
                    throw new InvalidDataException(
                        $"Relation '{PathText(relation.Path)}' has missing participant '{PathText(participantPath)}'.");
            }
        }

        return new RestorePlan(OrderRelations(document.Records));
    }

    private void ValidateDocument(GraphBackupDocument document)
    {
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported semantic graph backup version '{document.Version}'.");
        if (document.Records is null)
            throw new InvalidDataException("Semantic backup has no records collection.");

        var ids = new HashSet<string>(PhysicalIdComparer);
        foreach (var record in document.Records) {
            if (record is null)
                throw new InvalidDataException("Semantic backup contains a null record.");
            if (record.Path is null || record.Path.Length == 0 || record.Path.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Semantic backup contains an empty element id.");
            if (record.Attributes is null
                || record.TypeIds is null
                || record.RequiredTypeIds is null
                || record.Members is null)
                throw new InvalidDataException(
                    $"Semantic backup element '{PathText(record.Path)}' has an incomplete record body.");
            if (!ids.Add(PathText(record.Path)))
                throw new InvalidDataException(
                    $"Semantic backup contains element id '{PathText(record.Path)}' that collides on this filesystem.");
            var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in record.Attributes) {
                if (value is null)
                    throw new InvalidDataException(
                        $"Semantic backup element '{PathText(record.Path)}' contains null attribute '{name}'.");
                if (!attributeNames.Add(name))
                    throw new InvalidDataException(
                        $"Semantic backup element '{PathText(record.Path)}' contains case-colliding attribute '{name}'.");
            }
            if (!Enum.TryParse<SemanticElementKind>(record.Kind, ignoreCase: true, out var kind))
                throw new InvalidDataException($"Semantic backup element '{PathText(record.Path)}' has unknown kind '{record.Kind}'.");
            if (!string.Equals(record.Kind, kind.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Semantic backup element '{PathText(record.Path)}' must use canonical kind name '{kind}'.");

            ValidateKindAndPath(record, kind);
            ValidateReferences(record.TypeIds, record, "type", allowEmpty: false);
            ValidateReferences(record.RequiredTypeIds, record, "required type", allowEmpty: false);
            if (record.TypeIds.Count > 0
                && kind is not (SemanticElementKind.Instance or SemanticElementKind.Relation))
                throw new InvalidDataException(
                    $"Non-instance element '{PathText(record.Path)}' contains declared type references.");
            if (record.RequiredTypeIds.Count > 0 && kind != SemanticElementKind.Type)
                throw new InvalidDataException(
                    $"Non-type element '{PathText(record.Path)}' contains required type references.");
            if (record.Members.Count > 0 && kind != SemanticElementKind.Relation)
                throw new InvalidDataException(
                    $"Non-relation element '{PathText(record.Path)}' contains relation members.");

            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in record.Members) {
                if (member is null
                    || string.IsNullOrWhiteSpace(member.Name)
                    || member.MemberTypeId is null
                    || member.ParticipantIds is null)
                    throw new InvalidDataException($"Relation '{PathText(record.Path)}' has an unnamed member.");
                if (!string.Equals(member.Name, member.Name.Trim(), StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Relation '{PathText(record.Path)}' member '{member.Name}' has leading or trailing whitespace.");
                if (!memberNames.Add(member.Name))
                    throw new InvalidDataException(
                        $"Relation '{PathText(record.Path)}' declares member '{member.Name}' more than once.");
                if (member.MemberTypeId.Length > 0)
                    ValidateReference(member.MemberTypeId, record, $"member type of '{member.Name}'");
                ValidateReferences(
                    member.ParticipantIds,
                    record,
                    $"participant of '{member.Name}'",
                    allowEmpty: false);
            }
        }
    }

    private void ValidateKindAndPath(GraphBackupRecord record, SemanticElementKind kind)
    {
        var path = PathText(record.Path);
        var typeRootId = graph.SemanticStorage.TypeRoot.GlobalId.ToString();
        switch (kind) {
            case SemanticElementKind.TypeCatalog when record.Path.Length == 1
                                                       && string.Equals(record.Path[0], typeRootId, StringComparison.Ordinal):
                return;
            case SemanticElementKind.Type or SemanticElementKind.TypeInfrastructure
                when record.Path.Length > 1
                     && string.Equals(record.Path[0], typeRootId, StringComparison.Ordinal):
                return;
            case SemanticElementKind.Instance when !string.Equals(record.Path[0], typeRootId, StringComparison.Ordinal):
                return;
            case SemanticElementKind.Relation when record.Path.Length == 1
                                                   && !string.Equals(record.Path[0], typeRootId, StringComparison.Ordinal):
                return;
            default:
                throw new InvalidDataException(
                    $"Semantic backup element '{path}' has kind '{kind}' incompatible with its id.");
        }
    }

    private static void ValidateReferences(
        IEnumerable<string[]> references,
        GraphBackupRecord owner,
        string referenceKind,
        bool allowEmpty)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references) {
            if (!allowEmpty || reference.Length > 0)
                ValidateReference(reference, owner, referenceKind);
            if (!ids.Add(PathText(reference)))
                throw new InvalidDataException(
                    $"Semantic backup element '{PathText(owner.Path)}' contains duplicate {referenceKind} reference '{PathText(reference)}'.");
        }
    }

    private static void ValidateReference(
        string[] reference,
        GraphBackupRecord owner,
        string referenceKind)
    {
        if (reference is null || reference.Length == 0 || reference.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(
                $"Semantic backup element '{PathText(owner.Path)}' contains an empty {referenceKind} id.");
    }

    private static IReadOnlyList<GraphBackupRecord> OrderRelations(
        IReadOnlyCollection<GraphBackupRecord> records)
    {
        var pending = records
            .Where(static record => ParseKind(record) == SemanticElementKind.Relation)
            .ToDictionary(static record => PathText(record.Path), StringComparer.Ordinal);
        var ordered = new List<GraphBackupRecord>(pending.Count);
        var restoredIds = new HashSet<string>(StringComparer.Ordinal);

        while (pending.Count > 0) {
            var ready = pending.Values
                .Where(record => record.Members
                    .SelectMany(static member => member.ParticipantIds)
                    .Select(PathText)
                    .Where(pending.ContainsKey)
                    .All(restoredIds.Contains))
                .OrderBy(static record => PathText(record.Path), StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
                throw new InvalidDataException(
                    $"Semantic backup contains cyclic relation participants: {string.Join(", ", pending.Keys.Order(StringComparer.Ordinal))}.");

            foreach (var relation in ready) {
                var id = PathText(relation.Path);
                pending.Remove(id);
                restoredIds.Add(id);
                ordered.Add(relation);
            }
        }

        return ordered;
    }

    private static PreparedRelation PrepareRelation(GraphBackupRecord record) => new(
        new NodeLocalId(record.Path[0]),
        record.TypeIds.Select(ToInternalId).ToArray(),
        record.Members.Select(static member => new SemanticRelationMember(
            member.Name,
            ToInternalId(member.MemberTypeId),
            member.ParticipantIds.Select(ToInternalId).ToArray())).ToArray());

    private static InternalId ToInternalId(string[] path) =>
        new(path.Select(static segment => new NodeLocalId(segment)));

    private static SemanticElementKind ParseKind(GraphBackupRecord record) =>
        Enum.Parse<SemanticElementKind>(record.Kind, ignoreCase: true);

    private static string[] Segments(InternalId id) =>
        id.Select(static segment => segment.ToString()).ToArray();

    private static string PathText(string[] path) => string.Join('/', path);

    private sealed class GraphBackupDocument
    {
        public int Version { get; init; }
        public List<GraphBackupRecord> Records { get; init; } = [];
    }

    private sealed class GraphBackupRecord
    {
        public string[] Path { get; init; } = [];
        public string Kind { get; init; } = string.Empty;
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string[]> TypeIds { get; init; } = [];
        public List<string[]> RequiredTypeIds { get; init; } = [];
        public List<GraphBackupMember> Members { get; init; } = [];
    }

    private sealed class GraphBackupMember
    {
        public string Name { get; init; } = string.Empty;
        public string[] MemberTypeId { get; init; } = [];
        public List<string[]> ParticipantIds { get; init; } = [];
    }

    private sealed record RestorePlan(IReadOnlyList<GraphBackupRecord> Relations);

    private sealed record PreparedRelation(
        NodeLocalId LocalId,
        IReadOnlyList<InternalId> TypeIds,
        IReadOnlyList<SemanticRelationMember> Members);

    private sealed class KeyValuePairComparer : IEqualityComparer<KeyValuePair<string, string>>
    {
        public static readonly KeyValuePairComparer Instance = new();

        public bool Equals(KeyValuePair<string, string> first, KeyValuePair<string, string> second) =>
            string.Equals(first.Key, second.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Value, second.Value, StringComparison.Ordinal);

        public int GetHashCode(KeyValuePair<string, string> value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key),
                StringComparer.Ordinal.GetHashCode(value.Value));
    }

    private sealed class StringArrayComparer : IEqualityComparer<string[]>
    {
        public static readonly StringArrayComparer Instance = new();

        public bool Equals(string[]? first, string[]? second) =>
            ReferenceEquals(first, second)
            || first is not null && second is not null && first.SequenceEqual(second, StringComparer.Ordinal);

        public int GetHashCode(string[] value)
        {
            var hash = new HashCode();
            foreach (var segment in value)
                hash.Add(segment, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
