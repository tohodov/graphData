using System.Reflection;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphStorageInitializer
{
    private const string RuntimeTypesVersion = "6";

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

        var graph = new GraphService(
            _storage,
            new GraphSearchService(_storage),
            new InitializerCancellationTokenAccessor(cancellationToken),
            _runtimeTypes);
        var result = await graph.AddSubgraph(CreateRuntimeTypesSubgraph()).ConfigureAwait(false);
        RequireOk(result, "add runtime type subgraph");
    }

    private async Task<bool> IsRuntimeTypesInitializerCompletedAsync()
    {
        var result = await _storage.Get(RuntimeTypesCompletionMarkerId()).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.NotFound)
            return false;

        RequireOk(result, $"read initializer marker '{RuntimeTypesCompletionMarkerId()}'");
        return true;
    }

    private InternalId RuntimeTypesCompletionMarkerId() =>
        new(GraphSystemNodeIds.RuntimeTypesInitializer.Concat([
            new NodeLocalId(RuntimeTypesVersion),
            new NodeLocalId(_runtimeTypes.Fingerprint)
        ]));

    private Node CreateRuntimeTypesSubgraph()
    {
        var subgraph = new RuntimeTypesSubgraphBuilder();

        foreach (var type in _runtimeTypes.Types) {
            subgraph.GetNode(type.TypeId);
            if (type.TypeId != GraphBaseTypeIds.NodeType)
                subgraph.Connect(type.TypeId, GraphBaseTypeIds.NodeType);

            if (type.NodeTypeDescriptor is null
                || !_runtimeTypes.TryCreateNodeTypeDefinition(
                    type.TypeId,
                    NodeType.FromState(subgraph.GetNode(type.TypeId).State),
                    out var nodeTypeDefinition)
                || !TypedEdgeDefinition.TryCreate(nodeTypeDefinition, out var typedEdgeDefinition))
                continue;

            if (type.TypeId != GraphBaseTypeIds.Connection)
                subgraph.Connect(type.TypeId, GraphBaseTypeIds.Connection);

            foreach (var endpoint in typedEdgeDefinition.Endpoints) {
                var endpointId = new InternalId(type.TypeId.Concat([new NodeLocalId(endpoint.Name)]));
                subgraph.GetNode(endpointId);
                if (endpoint.NodeTypeId is { } nodeTypeId)
                    subgraph.Connect(endpointId, nodeTypeId);
            }
        }

        subgraph.GetNode(RuntimeTypesCompletionMarkerId());
        return subgraph.Root;
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

    private sealed class InitializerCancellationTokenAccessor(CancellationToken cancellationToken) : ICancellationTokenAccessor
    {
        public IEnumerable<CancellationToken> Tokens => [cancellationToken];
        public CancellationToken Token => cancellationToken;
    }

    private sealed class RuntimeTypesSubgraphBuilder
    {
        private readonly Dictionary<InternalId, Node> _nodes = [];

        public Node Root => GetNode(GraphSystemNodeIds.GraphDataRoot);

        public Node GetNode(InternalId id)
        {
            if (_nodes.TryGetValue(id, out var existing))
                return existing;

            var node = new Node(id);
            _nodes.Add(id, node);

            var parentId = ParentOf(id);
            if (parentId is not null)
                GetNode(parentId).Nodes.Add(node);

            return node;
        }

        public void Connect(InternalId first, InternalId second) =>
            GetNode(first).Nodes.Add(GetNode(second));

        private static InternalId? ParentOf(InternalId id)
        {
            var segments = id.ToArray();
            return segments.Length <= 1
                ? null
                : new InternalId(segments.Take(segments.Length - 1));
        }
    }

}
