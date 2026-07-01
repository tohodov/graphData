using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;

public sealed class Graph {
    static readonly NodeLocalId NodeTypeRootLocalId = new("NodeTypes");

    readonly IGraphStorage storage;
    readonly GraphSchemaRegistry schemaRegistry;
    readonly IReadOnlyDictionary<Type, NodeType> runtimeTypesByClrType;

    Graph(
        IGraphStorage storage,
        GraphSchemaRegistry schemaRegistry,
        NodeBacking root,
        NodeBacking nodeTypes,
        IReadOnlyDictionary<Type, NodeType> runtimeTypesByClrType) {
        this.storage = storage;
        this.schemaRegistry = schemaRegistry;
        this.runtimeTypesByClrType = runtimeTypesByClrType;
        Root = new Node(root);
        NodeTypes = new NodeType(nodeTypes);
    }

    public Node Root { get; }
    public NodeType NodeTypes { get; }
    public IReadOnlyCollection<NodeType> RuntimeTypes => runtimeTypesByClrType.Values.ToArray();

    internal IGraphStorage Storage => storage;

    internal static async Task<Graph> OpenAsync(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        var nodeTypes = await EnsureNodeTypesRootAsync(storage).ConfigureAwait(false);
        var runtimeTypes = new Dictionary<Type, NodeType>();
        foreach (var type in schemaRegistry.Types) {
            var nodeType = await EnsureNodeTypeAsync(storage, nodeTypes, type.Id).ConfigureAwait(false);
            runtimeTypes[type.ClrType] = new NodeType(nodeType);
        }

        return new Graph(storage, schemaRegistry, storage.Root, nodeTypes, runtimeTypes);
    }

    public NodeType? GetRuntimeType<TNodeType>()
        where TNodeType : NodeType {
        return GetRuntimeType(typeof(TNodeType));
    }

    public NodeType? GetRuntimeType(Type clrType) {
        ArgumentNullException.ThrowIfNull(clrType);
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
        return schemaRegistry.GetOrBuildDefinition(nodeType, GetRequiredRuntimeType);
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

    public async Task<IReadOnlyCollection<Node>> GetTopLevelUserRootsAsync() {
        var roots = await GetTopLevelUserRootStatesAsync().ConfigureAwait(false);
        return roots.Select(static state => new Node(state)).ToArray();
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
        return await IsNodeTypeAsync(node).ConfigureAwait(false)
            ? new NodeType(node)
            : null;
    }

    internal async Task<bool> IsNodeTypeAsync(NodeBacking node) {
        if (node.GlobalId == NodeTypes.GlobalId)
            return false;

        return await node.Nodes
            .AnyAsync(neighbor => neighbor.GlobalId == NodeTypes.GlobalId)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyCollection<NodeBacking>> GetTopLevelUserRootStatesAsync() {
        return (await storage.Root.Nodes.ToArrayAsync().ConfigureAwait(false))
            .Where(node => node.GlobalId != NodeTypes.GlobalId)
            .ToArray();
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
