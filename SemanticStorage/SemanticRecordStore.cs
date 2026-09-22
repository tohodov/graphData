using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphData.Typed;

namespace SemanticStorage;

/// <summary>Native typed records. This format has no raw graph or backing-node projection.</summary>
public sealed class SemanticRecordStore : ITypedGraphStore, IDisposable
{
    const string MarkerName = "typed-graph-store.json";
    const string LockName = ".typed-graph-store.lock";
    const string FormatName = "graphdata-native-typed-records";
    const int FormatVersion = 1;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    readonly string rootPath;
    readonly string typesPath;
    readonly string instancesPath;
    readonly FileStream rootLease;
    readonly SemaphoreSlim gate = new(1, 1);
    bool disposed;

    public SemanticRecordStore(string rootPath)
    {
        this.rootPath = Path.GetFullPath(rootPath);
        typesPath = Path.Combine(this.rootPath, "types");
        instancesPath = Path.Combine(this.rootPath, "instances");
        RefuseUnmarkedContents();
        Directory.CreateDirectory(this.rootPath);
        RejectReparsePoint(this.rootPath);
        var lockPath = Path.Combine(this.rootPath, LockName);
        if (File.Exists(lockPath)) RejectReparsePoint(lockPath);
        FileStream lease;
        try {
            lease = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        } catch (IOException exception) {
            throw Error(TypedGraphError.Conflict, $"Native store '{this.rootPath}' is already open or cannot be locked: {exception.Message}");
        }
        try {
            RefuseUnmarkedContents();
            var markerPath = Path.Combine(this.rootPath, MarkerName);
            if (!File.Exists(markerPath)) {
                WriteMarker(markerPath);
                Directory.CreateDirectory(typesPath);
                Directory.CreateDirectory(instancesPath);
            }
            ValidateRoot();
            rootLease = lease;
        } catch {
            lease.Dispose();
            throw;
        }
    }

    public Task<TypedType?> ReadTypeAsync(string id) => Locked(async () =>
        await ReadTypeCoreAsync(id).ConfigureAwait(false));

    public Task<IReadOnlyList<TypedType>> ReadTypesAsync() => Locked(ReadTypesCoreAsync);

    public Task<TypedElement?> ReadElementAsync(string id) => Locked(async () =>
        await ReadElementCoreAsync(id).ConfigureAwait(false));

    public Task<IReadOnlyList<TypedElement>> ReadIncidentRelationsAsync(string participantId) => Locked(async () =>
    {
        ValidateId(participantId);
        var records = await ReadElementsCoreAsync().ConfigureAwait(false);
        return (IReadOnlyList<TypedElement>)records.Where(record => record.Kind == TypedElementKind.Relation
                && record.Members.Values.Any(ids => ids.Contains(participantId, StringComparer.Ordinal)))
            .ToArray();
    });

    public Task InsertTypeAsync(TypedType definition) => Locked(async () =>
    {
        ValidateType(definition);
        await EnsureIdAvailableAsync(definition.Id).ConfigureAwait(false);
        await WriteAtomicAsync(RecordPath(typesPath, definition.Id),
            new RecordEnvelope<TypedType>(FormatVersion, "type", definition), replace: false).ConfigureAwait(false);
    });

    public Task InsertElementAsync(TypedElement element) => Locked(async () =>
    {
        ValidateElement(element);
        await EnsureIdAvailableAsync(element.Id).ConfigureAwait(false);
        foreach (var participant in element.Members.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal)) {
            if (await ReadElementCoreAsync(participant).ConfigureAwait(false) is null)
                throw Error(TypedGraphError.Invalid, $"Participant '{participant}' does not exist.");
        }
        await WriteAtomicAsync(RecordPath(instancesPath, element.Id),
            new RecordEnvelope<TypedElement>(FormatVersion, "element", element), replace: false).ConfigureAwait(false);
    });

    public Task ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes) => Locked(async () =>
    {
        var current = await ReadElementCoreAsync(id).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Element '{id}' does not exist.");
        ValidateAttributes(attributes);
        var replacement = current with { Attributes = new Dictionary<string, string>(attributes, StringComparer.Ordinal) };
        await WriteAtomicAsync(RecordPath(instancesPath, id),
            new RecordEnvelope<TypedElement>(FormatVersion, "element", replacement), replace: true).ConfigureAwait(false);
    });

    public Task DeleteElementAsync(string id) => Locked(async () =>
    {
        if (await ReadElementCoreAsync(id).ConfigureAwait(false) is null)
            throw Error(TypedGraphError.NotFound, $"Element '{id}' does not exist.");
        var records = await ReadElementsCoreAsync().ConfigureAwait(false);
        if (records.Any(record => record.Members.Values.Any(ids => ids.Contains(id, StringComparer.Ordinal))))
            throw Error(TypedGraphError.Conflict, $"Element '{id}' is referenced by an existing element.");
        File.Delete(RecordPath(instancesPath, id));
    });

    async Task<TypedType?> ReadTypeCoreAsync(string id)
    {
        ValidateId(id);
        var path = RecordPath(typesPath, id);
        ValidateRecordAddress(typesPath, instancesPath, id);
        if (!File.Exists(path)) return null;
        var record = await ReadRecordAsync<TypedType>(path, "type").ConfigureAwait(false);
        ValidateStored(() => ValidateType(record), path);
        VerifyIdentity(path, typesPath, id, record.Id);
        return record;
    }

    async Task<TypedElement?> ReadElementCoreAsync(string id)
    {
        ValidateId(id);
        var path = RecordPath(instancesPath, id);
        ValidateRecordAddress(instancesPath, typesPath, id);
        if (!File.Exists(path)) return null;
        var record = await ReadRecordAsync<TypedElement>(path, "element").ConfigureAwait(false);
        ValidateStored(() => ValidateElement(record), path);
        VerifyIdentity(path, instancesPath, id, record.Id);
        return record;
    }

    async Task<IReadOnlyList<TypedType>> ReadTypesCoreAsync()
    {
        var records = new List<TypedType>();
        foreach (var path in RecordFiles(typesPath)) {
            var record = await ReadRecordAsync<TypedType>(path, "type").ConfigureAwait(false);
            ValidateStored(() => ValidateType(record), path);
            VerifyIdentity(path, typesPath, record.Id, record.Id);
            ValidateRecordAddress(typesPath, instancesPath, record.Id);
            records.Add(record);
        }
        EnsureUniqueIds(records.Select(record => record.Id));
        return records.OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
    }

    async Task<IReadOnlyList<TypedElement>> ReadElementsCoreAsync()
    {
        var records = new List<TypedElement>();
        foreach (var path in RecordFiles(instancesPath)) {
            var record = await ReadRecordAsync<TypedElement>(path, "element").ConfigureAwait(false);
            ValidateStored(() => ValidateElement(record), path);
            VerifyIdentity(path, instancesPath, record.Id, record.Id);
            ValidateRecordAddress(instancesPath, typesPath, record.Id);
            records.Add(record);
        }
        EnsureUniqueIds(records.Select(record => record.Id));
        return records.OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
    }

    async Task EnsureIdAvailableAsync(string id)
    {
        if (await ReadTypeCoreAsync(id).ConfigureAwait(false) is not null
            || await ReadElementCoreAsync(id).ConfigureAwait(false) is not null)
            throw Error(TypedGraphError.Conflict, $"Identifier '{id}' already exists.");
        var types = await ReadTypesCoreAsync().ConfigureAwait(false);
        var elements = await ReadElementsCoreAsync().ConfigureAwait(false);
        if (types.Select(record => record.Id).Concat(elements.Select(record => record.Id))
            .Any(existingId => string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase)))
            throw Error(TypedGraphError.Conflict, $"Identifier '{id}' differs from an existing identifier only by case.");
    }

    static async Task<T> ReadRecordAsync<T>(string path, string kind)
    {
        try {
            RejectReparsePoint(path);
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            ValidateJson(document.RootElement);
            RequireProperties(document.RootElement, "formatVersion", "recordKind", "value");
            if (document.RootElement.GetProperty("formatVersion").GetInt32() != FormatVersion
                || document.RootElement.GetProperty("recordKind").GetString() != kind)
                throw Error(TypedGraphError.Corrupt, $"Unsupported native record format or kind in '{path}'.");
            ValidateRecordShape(document.RootElement.GetProperty("value"), kind);
            var envelope = JsonSerializer.Deserialize<RecordEnvelope<T>>(bytes, JsonOptions)
                ?? throw Error(TypedGraphError.Corrupt, $"Null native record in '{path}'.");
            return envelope.Value;
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException
                                            && exception is not TypedGraphException) {
            throw Error(TypedGraphError.Corrupt, $"Malformed native record '{path}': {exception.Message}");
        }
    }

    static void ValidateRecordShape(JsonElement value, string kind)
    {
        if (kind == "type") {
            RequireProperties(value, "id", "kind", "isAbstract", "requiredTypeIds", "members", "attributes");
            RequireStrings(value.GetProperty("requiredTypeIds"));
            foreach (var member in value.GetProperty("members").EnumerateArray()) {
                RequireProperties(member, "name", "typeId", "min", "max");
                RequireString(member.GetProperty("name"));
                var typeId = member.GetProperty("typeId");
                if (typeId.ValueKind != JsonValueKind.Null) RequireString(typeId);
            }
            foreach (var attribute in value.GetProperty("attributes").EnumerateArray()) {
                RequireProperties(attribute, "name", "kind", "required");
                RequireString(attribute.GetProperty("name"));
                RequireString(attribute.GetProperty("kind"));
            }
        } else {
            RequireProperties(value, "id", "kind", "typeIds", "attributes", "members");
            RequireStrings(value.GetProperty("typeIds"));
            foreach (var attribute in value.GetProperty("attributes").EnumerateObject()) RequireString(attribute.Value);
            foreach (var member in value.GetProperty("members").EnumerateObject()) RequireStrings(member.Value);
        }
        RequireString(value.GetProperty("id"));
        RequireString(value.GetProperty("kind"));
    }

    void RefuseUnmarkedContents()
    {
        if (!Directory.Exists(rootPath)) return;
        RejectReparsePoint(rootPath);
        if (File.Exists(Path.Combine(rootPath, MarkerName))) return;
        if (Directory.EnumerateFileSystemEntries(rootPath).Any(path => Path.GetFileName(path) != LockName))
            throw Error(TypedGraphError.Unsupported,
                $"'{rootPath}' is not an empty native typed store. Legacy and experimental formats require explicit migration.");
    }

    void ValidateRoot()
    {
        var markerPath = Path.Combine(rootPath, MarkerName);
        try {
            RejectReparsePoint(markerPath);
            using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            ValidateJson(marker.RootElement);
            RequireProperties(marker.RootElement, "format", "formatVersion");
            if (marker.RootElement.GetProperty("format").GetString() != FormatName
                || marker.RootElement.GetProperty("formatVersion").GetInt32() != FormatVersion)
                throw Error(TypedGraphError.Unsupported, $"Unsupported native store format at '{rootPath}'.");
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException
                                            && exception is not TypedGraphException) {
            throw Error(TypedGraphError.Corrupt, $"Malformed native store marker: {exception.Message}");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(rootPath)) {
            RejectReparsePoint(path);
            if (Path.GetFileName(path) is not (MarkerName or LockName or "types" or "instances"))
                throw Error(TypedGraphError.Corrupt, $"Unexpected native store entry '{path}'.");
        }
        foreach (var directory in new[] { typesPath, instancesPath }) {
            if (!Directory.Exists(directory))
                throw Error(TypedGraphError.Corrupt, $"Missing native records directory '{directory}'.");
            _ = RecordFiles(directory).ToArray();
        }
    }

    static IEnumerable<string> RecordFiles(string directory)
    {
        if (!Directory.Exists(directory))
            throw Error(TypedGraphError.Corrupt, $"Missing native records directory '{directory}'.");
        RejectReparsePoint(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal)) {
            RejectReparsePoint(path);
            var name = Path.GetFileName(path);
            if (File.Exists(path) && name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (!File.Exists(path) || name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal)
                || !name[..64].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw Error(TypedGraphError.Corrupt, $"Unexpected native record path '{path}'.");
            yield return path;
        }
    }

    static string RecordPath(string directory, string id) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");

    static void ValidateRecordAddress(string directory, string otherDirectory, string id)
    {
        if (!Directory.Exists(directory) || !Directory.Exists(otherDirectory))
            throw Error(TypedGraphError.Corrupt, "A native records directory is missing.");
        RejectReparsePoint(directory);
        RejectReparsePoint(otherDirectory);
        if (Directory.Exists(RecordPath(directory, id)))
            throw Error(TypedGraphError.Corrupt, $"Native record '{id}' is a directory.");
        if (File.Exists(RecordPath(directory, id))
            && (File.Exists(RecordPath(otherDirectory, id)) || Directory.Exists(RecordPath(otherDirectory, id))))
            throw Error(TypedGraphError.Corrupt, $"Identifier '{id}' appears in both record spaces.");
    }

    static void VerifyIdentity(string path, string directory, string requestedId, string storedId)
    {
        if (requestedId != storedId || !string.Equals(path, RecordPath(directory, storedId), StringComparison.Ordinal))
            throw Error(TypedGraphError.Corrupt, $"Native record identity does not match its address in '{path}'.");
    }

    static async Task WriteAtomicAsync<T>(string path, T record, bool replace)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (replace && !File.Exists(path))
                throw Error(TypedGraphError.NotFound, $"Native record '{path}' no longer exists.");
            File.Move(temporaryPath, path, overwrite: replace);
        } finally {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    static void WriteMarker(string path)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough)) {
                JsonSerializer.Serialize(stream, new StoreMarker(FormatName, FormatVersion), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path);
        } finally {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    static void ValidateType(TypedType definition)
    {
        ValidateId(definition.Id);
        ValidateKind(definition.Kind);
        ValidateIds(definition.RequiredTypeIds);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in definition.Members) {
            ValidateName(member.Name);
            if (!names.Add(member.Name)) throw Error(TypedGraphError.Invalid, $"Duplicate member '{member.Name}'.");
            if (member.TypeId is { } typeId) ValidateId(typeId);
            if (member.Min < 0 || member.Max is < 0 || member.Max is { } maximum && maximum < member.Min)
                throw Error(TypedGraphError.Invalid, $"Invalid cardinality for '{member.Name}'.");
        }
        names.Clear();
        foreach (var attribute in definition.Attributes) {
            ValidateName(attribute.Name);
            if (!names.Add(attribute.Name) || !Enum.IsDefined(attribute.Kind))
                throw Error(TypedGraphError.Invalid, $"Duplicate or invalid attribute '{attribute.Name}'.");
        }
    }

    static void ValidateElement(TypedElement element)
    {
        ValidateId(element.Id);
        ValidateKind(element.Kind);
        ValidateIds(element.TypeIds);
        ValidateAttributes(element.Attributes);
        foreach (var (name, participants) in element.Members) {
            ValidateName(name);
            ValidateIds(participants);
        }
    }

    static void ValidateKind(TypedElementKind kind)
    {
        if (!Enum.IsDefined(kind)) throw Error(TypedGraphError.Invalid, "Unknown typed element kind.");
    }

    static void ValidateIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) {
            ValidateId(id);
            if (!seen.Add(id)) throw Error(TypedGraphError.Invalid, $"Duplicate identifier '{id}'.");
        }
    }

    static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Any(char.IsControl))
            throw Error(TypedGraphError.Invalid, "Identifier must be nonempty and contain no surrounding whitespace or control characters.");
    }

    static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Any(char.IsControl))
            throw Error(TypedGraphError.Invalid, "Member and attribute names must be nonempty and contain no surrounding whitespace or control characters.");
    }

    static void ValidateAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var name in attributes.Keys) ValidateName(name);
    }

    static void EnsureUniqueIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Any(id => !seen.Add(id))) throw Error(TypedGraphError.Corrupt, "Duplicate canonical record identifiers.");
    }

    static void ValidateStored(Action validate, string path)
    {
        try { validate(); }
        catch (TypedGraphException exception) {
            throw Error(TypedGraphError.Corrupt, $"Invalid canonical record '{path}': {exception.Message}");
        }
    }

    static void RequireProperties(JsonElement value, params string[] names)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(names)) throw Error(TypedGraphError.Corrupt, "Unexpected or missing JSON properties.");
    }

    static void RequireString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Error(TypedGraphError.Corrupt, "Expected a JSON string.");
    }

    static void RequireStrings(JsonElement value)
    {
        foreach (var item in value.EnumerateArray()) RequireString(item);
    }

    static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!names.Add(property.Name)) throw Error(TypedGraphError.Corrupt, $"Duplicate JSON property '{property.Name}'.");
                ValidateJson(property.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) {
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
        }
    }

    static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error(TypedGraphError.Unsupported, $"Native store cannot use a reparse point: '{path}'.");
    }

    async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await action().ConfigureAwait(false);
        } finally { gate.Release(); }
    }

    Task Locked(Func<Task> action) => Locked(async () => { await action().ConfigureAwait(false); return true; });

    public void Dispose()
    {
        gate.Wait();
        try {
            if (disposed) return;
            disposed = true;
            rootLease.Dispose();
        } finally { gate.Release(); }
    }

    static TypedGraphException Error(TypedGraphError error, string message) => new(error, message);
    sealed record StoreMarker(string Format, int FormatVersion);
    sealed record RecordEnvelope<T>(int FormatVersion, string RecordKind, T Value);
}
