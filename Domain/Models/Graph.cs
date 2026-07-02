using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;

public sealed class Graph {
    static readonly NodeLocalId NodeTypeRootLocalId = new("NodeTypes");

    readonly IGraphStorage storage;
    readonly GraphSchemaRegistry schemaRegistry;
    readonly Dictionary<Type, NodeType> runtimeTypesByClrType = [];
    readonly SemaphoreSlim openGate = new(1, 1);
    Node? root;
    NodeType? nodeTypes;
    bool isOpen;

    internal Graph(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        this.storage = storage;
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

            var nodeTypesRoot = await EnsureNodeTypesRootAsync(storage).ConfigureAwait(false);
            runtimeTypesByClrType.Clear();
            foreach (var type in schemaRegistry.Types) {
                var nodeType = await EnsureNodeTypeAsync(storage, nodeTypesRoot, type.Id).ConfigureAwait(false);
                runtimeTypesByClrType[type.ClrType] = new NodeType(nodeType);
            }

            root = new Node(storage.Root);
            nodeTypes = new NodeType(nodeTypesRoot);
            isOpen = true;
        } finally {
            openGate.Release();
        }
    }

    public NodeType? GetRuntimeType<TNodeType>()
        where TNodeType : NodeType {
        return GetRuntimeType(typeof(TNodeType));
    }

    public NodeType? GetRuntimeType(Type clrType) {
        ArgumentNullException.ThrowIfNull(clrType);
        EnsureOpen();
        return runtimeTypesByClrType.TryGetValue(clrType, out var nodeType)
            ? nodeType
            : null;
    }

    public NodeTypeDefinition? GetNodeTypeDefinition<TNodeType>()
        where TNodeType : NodeType {
        var type = GetRuntimeType<TNodeType>();
        return type is null ? null : GetNodeTypeDefinition(type);
    }

    public NodeTypeDefinition GetNodeTypeDefinition(NodeType nodeType) {
        EnsureOpen();
        return schemaRegistry.GetOrBuildDefinition(nodeType, GetRequiredRuntimeType);
    }

    internal void RegisterNodeTypeDefinition(NodeTypeDefinition definition) {
        EnsureOpen();
        schemaRegistry.RegisterDynamicDefinition(definition);
    }

    public TypedEdgeDefinition? GetTypedEdgeDefinition<TNodeType>()
        where TNodeType : NodeType {
        var nodeType = GetNodeTypeDefinition<TNodeType>();
        if (nodeType is null)
            return null;

        return TypedEdgeDefinition.TryCreate(nodeType, out var definition)
            ? definition
            : null;
    }

    internal NodeType GetRequiredRuntimeType<TNodeType>()
        where TNodeType : NodeType {
        return GetRequiredRuntimeType(typeof(TNodeType));
    }

    internal NodeType GetRequiredRuntimeType(Type clrType) {
        var type = GetRuntimeType(clrType);
        return type
            ?? throw new InvalidOperationException($"Runtime graph type '{clrType.FullName}' is not present in the graph.");
    }

    internal async Task<NodeType?> AsNodeTypeAsync(NodeBacking node) {
        EnsureOpen();
        return await IsNodeTypeAsync(node).ConfigureAwait(false)
            ? new NodeType(node)
            : null;
    }

    internal async Task<bool> IsNodeTypeAsync(NodeBacking node) {
        EnsureOpen();
        if (node.GlobalId == NodeTypes.GlobalId)
            return false;

        return await node.Nodes
            .AnyAsync(neighbor => neighbor.GlobalId == NodeTypes.GlobalId)
            .ConfigureAwait(false);
    }

    internal void EnsureOpen() {
        if (!isOpen)
            throw new InvalidOperationException("Graph is not open.");
    }

    static async Task<NodeBacking> EnsureNodeTypesRootAsync(IGraphStorage storage) {
        return await storage.Get(new NodePath(NodeTypeRootLocalId)).ConfigureAwait(false)
            ?? await storage.Create(NodeTypeRootLocalId).ConfigureAwait(false);
    }

    static async Task<NodeBacking> EnsureNodeTypeAsync(IGraphStorage storage, NodeBacking nodeTypes, NodeLocalId localId) {
        return await storage.Get(nodeTypes.GlobalId, localId).ConfigureAwait(false)
            ?? await storage.Create(localId, nodeTypes.GlobalId).ConfigureAwait(false);
    }
}
