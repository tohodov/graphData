using System.Globalization;
using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphStorageInitializer
{
    private const string RuntimeTypesVersion = "1";
    private const string CompletedAttribute = "completed";
    private const string InitializerAttribute = "storage.initializer";
    private const string VersionAttribute = "storage.initializer.version";
    private const string RuntimeTypesFingerprintAttribute = "storage.initializer.runtimeTypes";

    private static readonly IReadOnlyCollection<SystemNodeDefinition> SystemNodes = [
        new(GraphSystemNodeIds.GraphDataRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "system-root"
        }),
        new(GraphSystemNodeIds.TypeRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "type-root"
        }),
        new(GraphSystemNodeIds.NodeTypeRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "type-root",
            [GraphRuntimeAttributeNames.GraphElement] = "node"
        }),
        new(GraphSystemNodeIds.EdgeTypeRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "type-root",
            [GraphRuntimeAttributeNames.GraphElement] = "edge"
        }),
        new(GraphSystemNodeIds.RelationRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "relation-root",
            [GraphRuntimeAttributeNames.GraphElement] = "edge"
        }),
        new(GraphSystemNodeIds.StorageRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "storage-root"
        }),
        new(GraphSystemNodeIds.InitializerRoot, new Dictionary<string, string> {
            [GraphRuntimeAttributeNames.GraphKind] = "initializer-root"
        })
    ];

    private readonly IGraphStorage _storage;
    private readonly GraphRuntimeTypeCatalog _runtimeTypes;

    internal GraphStorageInitializer(IGraphStorage storage)
        : this(storage, GraphRuntimeTypeCatalog.Create()) {
    }

    internal GraphStorageInitializer(IGraphStorage storage, params Assembly[] runtimeTypeAssemblies)
        : this(storage, GraphRuntimeTypeCatalog.Create(runtimeTypeAssemblies)) {
    }

    internal GraphStorageInitializer(IGraphStorage storage, GraphRuntimeTypeCatalog runtimeTypes)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _runtimeTypes = runtimeTypes ?? throw new ArgumentNullException(nameof(runtimeTypes));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await IsRuntimeTypesInitializerCompletedAsync().ConfigureAwait(false))
            return;

        foreach (var systemNode in SystemNodes) {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureNodeAsync(systemNode.Id, systemNode.Attributes).ConfigureAwait(false);
        }

        foreach (var type in DiscoverRuntimeTypeDefinitions()) {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureNodeAsync(type.TypeId, CreateRuntimeTypeAttributes(type)).ConfigureAwait(false);
        }

        await MarkRuntimeTypesInitializerCompletedAsync().ConfigureAwait(false);
    }

    private async Task<bool> IsRuntimeTypesInitializerCompletedAsync()
    {
        var result = await _storage.Get(GraphSystemNodeIds.RuntimeTypesInitializer).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.NotFound)
            return false;

        var node = RequireOk(result, $"read initializer marker '{GraphSystemNodeIds.RuntimeTypesInitializer}'");
        return node.Attributes.TryGetValue(CompletedAttribute, out var completed)
            && string.Equals(completed, bool.TrueString, StringComparison.OrdinalIgnoreCase)
            && node.Attributes.TryGetValue(VersionAttribute, out var version)
            && string.Equals(version, RuntimeTypesVersion, StringComparison.Ordinal)
            && node.Attributes.TryGetValue(RuntimeTypesFingerprintAttribute, out var fingerprint)
            && string.Equals(fingerprint, _runtimeTypes.Fingerprint, StringComparison.Ordinal);
    }

    private async Task MarkRuntimeTypesInitializerCompletedAsync()
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            [GraphRuntimeAttributeNames.GraphKind] = "storage-initializer",
            [InitializerAttribute] = "runtime-types",
            [CompletedAttribute] = bool.TrueString,
            [VersionAttribute] = RuntimeTypesVersion,
            [RuntimeTypesFingerprintAttribute] = _runtimeTypes.Fingerprint,
            ["completedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        await EnsureNodeAsync(GraphSystemNodeIds.RuntimeTypesInitializer, attributes).ConfigureAwait(false);
        var result = await _storage.Update(GraphSystemNodeIds.RuntimeTypesInitializer, attributes).ConfigureAwait(false);
        RequireOk(result, $"write initializer marker '{GraphSystemNodeIds.RuntimeTypesInitializer}'");
    }

    private async Task<NodeState> EnsureNodeAsync(
        NodeGlobalId id,
        IReadOnlyDictionary<string, string>? requiredAttributes = null)
    {
        var result = await _storage.Get(id).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Ok && result.Value is not null) {
            await MergeMissingAttributesAsync(id, result.Value, requiredAttributes).ConfigureAwait(false);
            return result.Value;
        }

        if (result.Status != ServiceResultStatus.NotFound)
            return RequireOk(result, $"read node '{id}'");

        var segments = id.ToArray();
        if (segments.Length == 0)
            return RequireOk(await _storage.Get(_storage.Root).ConfigureAwait(false), "read storage root");

        NodeGlobalId? parentId = null;
        if (segments.Length > 1) {
            parentId = new NodeGlobalId(segments.Take(segments.Length - 1));
            await EnsureNodeAsync(parentId.Value, GetSystemAttributes(parentId.Value)).ConfigureAwait(false);
        }

        NodePath? parentPath = parentId is null ? null : parentId.Value;
        var createResult = await _storage.Create(
            segments[^1],
            parentPath,
            requiredAttributes is null
                ? null
                : new Dictionary<string, string>(requiredAttributes, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);

        return RequireOk(createResult, $"create node '{id}'");
    }

    private async Task MergeMissingAttributesAsync(
        NodeGlobalId id,
        NodeState node,
        IReadOnlyDictionary<string, string>? requiredAttributes)
    {
        if (requiredAttributes is null || requiredAttributes.Count == 0)
            return;

        var attributes = new Dictionary<string, string>(node.Attributes, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var (key, value) in requiredAttributes) {
            if (attributes.ContainsKey(key))
                continue;

            attributes[key] = value;
            changed = true;
        }

        if (!changed)
            return;

        var result = await _storage.Update(id, attributes).ConfigureAwait(false);
        RequireOk(result, $"update node '{id}'");
    }

    private static IReadOnlyDictionary<string, string>? GetSystemAttributes(NodeGlobalId id) =>
        SystemNodes.FirstOrDefault(node => node.Id == id)?.Attributes;

    private IReadOnlyCollection<RuntimeGraphTypeDefinition> DiscoverRuntimeTypeDefinitions() =>
        _runtimeTypes.Types;

    private static Dictionary<string, string> CreateRuntimeTypeAttributes(RuntimeGraphTypeDefinition type)
    {
        if (!IsChildOf(type.TypeId, type.RootId))
            throw new InvalidOperationException(
                $"Runtime graph type '{type.ClrType.FullName}' uses id '{type.TypeId}', but it must be under '{type.RootId}'.");

        var defaults = GetRuntimeTypeDefaults(type);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            [GraphRuntimeAttributeNames.GraphKind] = "type",
            [GraphRuntimeAttributeNames.GraphElement] = type.Element,
            ["label"] = defaults.Label,
            ["color"] = defaults.Color,
            [GraphRuntimeAttributeNames.ProjectionRank] = defaults.Rank.ToString(CultureInfo.InvariantCulture)
        };

        if (type.Element == "edge")
            attributes["directed"] = defaults.Directed ? "true" : "false";

        return attributes;
    }

    private static RuntimeTypeDefaults GetRuntimeTypeDefaults(RuntimeGraphTypeDefinition type)
    {
        if (type.TypeId == GraphBaseTypeIds.NodeType)
            return new RuntimeTypeDefaults("Type", "#334155", 90, false);
        if (type.TypeId == GraphBaseTypeIds.NodeInstance)
            return new RuntimeTypeDefaults("Instance", "#0f766e", 70, false);
        if (type.TypeId == GraphBaseTypeIds.EdgeType)
            return new RuntimeTypeDefaults("Type", "#7c2d12", 60, true);
        if (type.TypeId == GraphBaseTypeIds.EdgeInstance)
            return new RuntimeTypeDefaults("Instance", "#b45309", 50, true);

        return new RuntimeTypeDefaults(CreateLabel(type), type.Element == "edge" ? "#92400e" : "#475569", type.Element == "edge" ? 30 : 50, type.Element == "edge");
    }

    private static string CreateLabel(RuntimeGraphTypeDefinition type)
    {
        var name = type.ClrType.Name;
        if (type.Element == "node" && name.EndsWith(nameof(NodeType), StringComparison.Ordinal))
            return name[..^nameof(NodeType).Length];
        if (type.Element == "node" && name.EndsWith(nameof(Node), StringComparison.Ordinal))
            return name[..^nameof(Node).Length];
        if (type.Element == "edge" && name.EndsWith(nameof(Edge), StringComparison.Ordinal))
            return name[..^nameof(Edge).Length];
        return name;
    }

    private static bool IsChildOf(NodeGlobalId id, NodeGlobalId root)
    {
        var idSegments = id.ToArray();
        var rootSegments = root.ToArray();
        if (idSegments.Length <= rootSegments.Length)
            return false;

        for (var index = 0; index < rootSegments.Length; index++)
            if (idSegments[index] != rootSegments[index])
                return false;

        return true;
    }

    private static T RequireOk<T>(ServiceResult<T> result, string operation) where T : class
    {
        if (result.Status == ServiceResultStatus.Ok && result.Value is not null)
            return result.Value;

        throw new InvalidOperationException($"Failed to {operation}: {result.Status}. {result.Error}");
    }

    private static void RequireOk(ServiceResult result, string operation)
    {
        if (result.Status == ServiceResultStatus.Ok)
            return;

        throw new InvalidOperationException($"Failed to {operation}: {result.Status}. {result.Error}");
    }

    private sealed record SystemNodeDefinition(NodeGlobalId Id, IReadOnlyDictionary<string, string> Attributes);

    private sealed record RuntimeTypeDefaults(string Label, string Color, int Rank, bool Directed);
}
