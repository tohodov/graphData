using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;

public sealed class Graph {
    readonly IGraphStorage storage;
    readonly ISemanticGraphStorage semanticStorage;
    readonly GraphSchemaRegistry schemaRegistry;
    readonly Dictionary<Type, NodeType> runtimeTypesByClrType = [];
    readonly SemaphoreSlim openGate = new(1, 1);
    Node? root;
    NodeType? nodeTypes;
    bool isOpen;

    internal Graph(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        this.storage = storage;
        semanticStorage = storage as ISemanticGraphStorage
            ?? throw new InvalidOperationException(
                $"Storage '{storage.GetType().FullName}' does not implement the semantic graph contract.");
        this.schemaRegistry = schemaRegistry;
    }

    public Node Root => root ?? throw new InvalidOperationException("Graph is not open.");
    public NodeType NodeTypes => nodeTypes ?? throw new InvalidOperationException("Graph is not open.");
    public IReadOnlyCollection<NodeType> RuntimeTypes {
        get {
            EnsureOpen();
            return runtimeTypesByClrType.Values.ToArray();
        }
    }

    internal IGraphStorage Storage => storage;
    internal ISemanticGraphStorage SemanticStorage => semanticStorage;
    internal bool IsOpen => isOpen;

    internal static async Task<Graph> OpenAsync(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        var graph = new Graph(storage, schemaRegistry);
        await graph.OpenAsync().ConfigureAwait(false);
        return graph;
    }

    public async Task OpenAsync() {
        if (isOpen)
            return;

        await openGate.WaitAsync().ConfigureAwait(false);
        try {
            if (isOpen)
                return;

            var nodeTypesRoot = semanticStorage.TypeRoot;
            runtimeTypesByClrType.Clear();
            foreach (var type in schemaRegistry.Types) {
                var nodeType = await EnsureNodeTypeAsync(
                    storage,
                    semanticStorage,
                    nodeTypesRoot,
                    type.Id).ConfigureAwait(false);
                runtimeTypesByClrType[type.ClrType] = new NodeType(nodeType);
            }

            foreach (var type in schemaRegistry.Types) {
                var nodeType = runtimeTypesByClrType[type.ClrType];
                if (DynamicNodeTypeDefinitionStorage.TryRead(nodeType) is not null)
                    continue;

                var definition = schemaRegistry.GetOrBuildDefinition(
                    nodeType,
                    GetMaterializedRuntimeType);
                await DynamicNodeTypeDefinitionStorage.WriteAsync(semanticStorage, definition).ConfigureAwait(false);
                await semanticStorage.SetRequiredTypeIdsAsync(
                    nodeType.GlobalId,
                    definition.RequiredTypes.Select(static type => (NodeRef)type.GlobalId)).ConfigureAwait(false);
            }

            root = new Node(storage.Root);
            nodeTypes = new NodeType(nodeTypesRoot);
            isOpen = true;
        } finally {
            openGate.Release();
        }
    }

    public NodeType? GetRuntimeType<TGraphType>() {
        return GetRuntimeType(typeof(TGraphType));
    }

    public NodeType? GetRuntimeType(Type clrType) {
        ArgumentNullException.ThrowIfNull(clrType);
        EnsureOpen();
        return runtimeTypesByClrType.TryGetValue(clrType, out var nodeType)
            ? nodeType
            : null;
    }

    public NodeTypeDefinition? GetNodeTypeDefinition<TGraphType>() {
        var type = GetRuntimeType<TGraphType>();
        return type is null ? null : GetNodeTypeDefinition(type);
    }

    public NodeTypeDefinition GetNodeTypeDefinition(NodeType nodeType) {
        EnsureOpen();
        var definition = schemaRegistry.GetOrBuildDefinition(nodeType, GetRequiredRuntimeType);
        var requiredTypes = definition.RequiredTypes
            .Concat(ReadRequiredTypes(nodeType))
            .DistinctBy(static type => type.GlobalId)
            .ToArray();
        return requiredTypes.Length == definition.RequiredTypes.Count
            && requiredTypes.Select(static type => type.GlobalId)
                .SequenceEqual(definition.RequiredTypes.Select(static type => type.GlobalId))
            ? definition
            : definition with { RequiredTypes = requiredTypes };
    }

    internal void RegisterNodeTypeDefinition(NodeTypeDefinition definition) {
        EnsureOpen();
        schemaRegistry.RegisterDynamicDefinition(definition);
    }

    public TypedEdgeDefinition? GetTypedEdgeDefinition<TEdgeType>()
        where TEdgeType : Edge {
        var nodeType = GetNodeTypeDefinition<TEdgeType>();
        if (nodeType is null)
            return null;

        return TypedEdgeDefinition.TryCreate(nodeType, out var definition)
            ? definition
            : null;
    }

    internal NodeType GetRequiredRuntimeType<TGraphType>() {
        return GetRequiredRuntimeType(typeof(TGraphType));
    }

    internal NodeType GetRequiredRuntimeType(Type clrType) {
        var type = GetRuntimeType(clrType);
        return type
            ?? throw new InvalidOperationException($"Runtime graph type '{clrType.FullName}' is not present in the graph.");
    }

    private NodeType GetMaterializedRuntimeType(Type clrType) {
        return runtimeTypesByClrType.TryGetValue(clrType, out var type)
            ? type
            : throw new InvalidOperationException($"Runtime graph type '{clrType.FullName}' is not present in the graph.");
    }

    internal async Task<NodeType?> AsNodeTypeAsync(NodeBacking node) {
        EnsureOpen();
        return await IsNodeTypeAsync(node).ConfigureAwait(false)
            ? new NodeType(node)
            : null;
    }

    internal async Task<bool> IsNodeTypeAsync(NodeBacking node) {
        EnsureOpen();
        await Task.CompletedTask;
        return semanticStorage.IsType(node);
    }

    internal void EnsureOpen() {
        if (!isOpen)
            throw new InvalidOperationException("Graph is not open.");
    }

    static async Task<NodeBacking> EnsureNodeTypeAsync(
        IGraphStorage graphStorage,
        ISemanticGraphStorage semanticStorage,
        NodeBacking nodeTypes,
        NodeLocalId localId) {
        var existing = await graphStorage.Get(nodeTypes.GlobalId, localId).ConfigureAwait(false);
        return existing
            ?? await semanticStorage.CreateTypeElementAsync(
                localId,
                nodeTypes.GlobalId,
                SemanticElementKind.Type,
                Array.Empty<NodeRef>()).ConfigureAwait(false);
    }

    private IReadOnlyCollection<NodeType> ReadRequiredTypes(NodeType nodeType) {
        var result = new List<NodeType>();
        foreach (var requiredTypeId in semanticStorage.GetRequiredTypeIds(nodeType.Backing)) {
            var backing = storage.Get(requiredTypeId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException(
                    $"Graph type '{nodeType.GlobalId}' references missing required type '{requiredTypeId}'.");
            if (!semanticStorage.IsType(backing))
                throw new InvalidOperationException(
                    $"Graph type '{nodeType.GlobalId}' references '{requiredTypeId}', which is not a graph type.");
            result.Add(new NodeType(backing));
        }
        return result;
    }
}
