using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphStorageInitializer
{
    private const string RuntimeTypesVersion = "4";

    private static readonly IReadOnlyCollection<InternalId> SystemNodes = [
        GraphSystemNodeIds.GraphDataRoot,
        GraphSystemNodeIds.TypeRoot,
        GraphSystemNodeIds.NodeTypeRoot,
        GraphSystemNodeIds.EdgeTypeRoot,
        GraphSystemNodeIds.RelationRoot,
        GraphSystemNodeIds.StorageRoot,
        GraphSystemNodeIds.InitializerRoot
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

        foreach (var systemNodeId in SystemNodes) {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureNodeAsync(systemNodeId).ConfigureAwait(false);
        }

        foreach (var type in DiscoverRuntimeTypeDefinitions()) {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureNodeAsync(type.TypeId).ConfigureAwait(false);
            await EnsureRuntimeTypeMembershipAsync(type).ConfigureAwait(false);
            await EnsureEdgeTypeDefinitionAsync(type).ConfigureAwait(false);
        }

        await MarkRuntimeTypesInitializerCompletedAsync().ConfigureAwait(false);
    }

    private async Task<bool> IsRuntimeTypesInitializerCompletedAsync()
    {
        var result = await _storage.Get(RuntimeTypesCompletionMarkerId()).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.NotFound)
            return false;

        RequireOk(result, $"read initializer marker '{RuntimeTypesCompletionMarkerId()}'");
        return true;
    }

    private async Task MarkRuntimeTypesInitializerCompletedAsync()
    {
        await EnsureNodeAsync(GraphSystemNodeIds.RuntimeTypesInitializer).ConfigureAwait(false);
        await EnsureNodeAsync(RuntimeTypesCompletionMarkerId()).ConfigureAwait(false);
    }

    private InternalId RuntimeTypesCompletionMarkerId() =>
        new(GraphSystemNodeIds.RuntimeTypesInitializer.Concat([
            new NodeLocalId(RuntimeTypesVersion),
            new NodeLocalId(_runtimeTypes.Fingerprint)
        ]));

    private async Task<NodeState> EnsureNodeAsync(InternalId id)
    {
        var result = await _storage.Get(id).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Ok && result.Value is not null)
            return result.Value;

        if (result.Status != ServiceResultStatus.NotFound)
            return RequireOk(result, $"read node '{id}'");

        var segments = id.ToArray();
        if (segments.Length == 0)
            return RequireOk(await _storage.Get(_storage.Root).ConfigureAwait(false), "read storage root");

        NodePath? parentId = null;
        if (segments.Length > 1) {
            parentId = new NodePath(segments.Take(segments.Length - 1)); //TODO переделать этот бред с NodePath/InternalId
            await EnsureNodeAsync(new InternalId(parentId)).ConfigureAwait(false);
        }

        NodePath? parentPath = parentId is null ? null : parentId;
        var createResult = await _storage.Create(
            segments[^1],
            parentPath).ConfigureAwait(false);

        return RequireOk(createResult, $"create node '{id}'");
    }

    private IReadOnlyCollection<RuntimeGraphTypeDefinition> DiscoverRuntimeTypeDefinitions() =>
        _runtimeTypes.Types;

    private async Task EnsureRuntimeTypeMembershipAsync(RuntimeGraphTypeDefinition type)
    {
        var baseTypeId = type.Element == "edge"
            ? GraphBaseTypeIds.EdgeType
            : GraphBaseTypeIds.NodeType;
        if (type.TypeId == baseTypeId)
            return;

        await EnsureNodeAsync(baseTypeId).ConfigureAwait(false);
        var connect = await _storage.Connect(type.TypeId, baseTypeId).ConfigureAwait(false);
        RequireOk(connect, $"connect runtime type '{type.TypeId}' to base type '{baseTypeId}'");
    }

    private async Task EnsureEdgeTypeDefinitionAsync(RuntimeGraphTypeDefinition type)
    {
        if (type.EdgeTypeDescriptor is null)
            return;

        if (!_runtimeTypes.TryCreateEdgeTypeDefinition(type.TypeId, out var definition))
            return;

        foreach (var endpoint in definition.Endpoints) {
            var endpointId = new InternalId(type.TypeId.Concat([new NodeLocalId(endpoint.Name)]));
            await EnsureNodeAsync(endpointId).ConfigureAwait(false);
            if (endpoint.NodeTypeId is { } nodeTypeId) {
                await EnsureNodeAsync(nodeTypeId).ConfigureAwait(false);
                var connect = await _storage.Connect(endpointId, nodeTypeId).ConfigureAwait(false);
                RequireOk(connect, $"connect edge endpoint '{endpointId}' to node type '{nodeTypeId}'");
            }
        }
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

}
