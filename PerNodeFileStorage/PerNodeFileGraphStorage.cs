using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.PerNodeFileStorage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.PerNodeFileStorage;

public sealed class PerNodeFileGraphStorage : IGraphStorage, IGraphNodeCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly PerNodeFileGraphStorageOptions _options;
    private readonly ILogger<PerNodeFileGraphStorage> _logger;
    private readonly string _metadataRoot;
    private readonly string _connectionsRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new(StringComparer.OrdinalIgnoreCase);

    public PerNodeFileGraphStorage(IOptions<PerNodeFileGraphStorageOptions> options, ILogger<PerNodeFileGraphStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new ArgumentException("Root path must be provided.", nameof(options));
        }

        _options.RootPath = Path.GetFullPath(_options.RootPath);
        _metadataRoot = Path.Combine(_options.RootPath, _options.MetadataDirectoryName);
        _connectionsRoot = Path.Combine(_options.RootPath, _options.ConnectionsDirectoryName);

        Directory.CreateDirectory(_metadataRoot);
        Directory.CreateDirectory(_connectionsRoot);
    }

    public async Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var nodeName = GetNodeName(parent, name);
        var document = new NodeDocument(nodeName, CopyAttributes(attributes));
        var metadataLock = GetMetadataLock(nodeName);
        await metadataLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteMetadataAsync(document).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }

        var connectionLock = GetConnectionLock(nodeName);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureConnectionsFileAsync(nodeName).ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }

        _logger.LogDebug("Stored node {NodeName} in a dedicated metadata file.", nodeName);
        return CreateNode(document);
    }

    public async Task<Node?> Get(Node? parent, string subNodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subNodeName);

        var nodeName = GetNodeName(parent, subNodeName);
        var document = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
        return document is null ? null : CreateNode(document);
    }

    public async Task<Node?> Get(NodeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        Node? node = null;
        foreach (var part in query.SelectRecursive(static x => x.Child))
        {
            node = await Get(node, part.Name).ConfigureAwait(false);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    public async Task Update(string subNodeName, IDictionary<string, string> attributes, Node? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subNodeName);
        ArgumentNullException.ThrowIfNull(attributes);

        var nodeName = GetNodeName(parent, subNodeName);
        var metadataLock = GetMetadataLock(nodeName);
        await metadataLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(GetMetadataPath(nodeName)))
            {
                throw new FileNotFoundException($"Node '{nodeName}' does not exist.");
            }

            await WriteMetadataAsync(new NodeDocument(nodeName, CopyAttributes(attributes))).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }
    }

    public async Task Connect(Node sourceNode, Node targetNode)
    {
        ArgumentNullException.ThrowIfNull(sourceNode);
        ArgumentNullException.ThrowIfNull(targetNode);

        if (string.Equals(sourceNode.Name, targetNode.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await EnsureNodeExistsAsync(sourceNode.Name).ConfigureAwait(false);
        await EnsureNodeExistsAsync(targetNode.Name).ConfigureAwait(false);

        var orderedNames = Order(sourceNode.Name, targetNode.Name).ToArray();
        var locks = orderedNames.Select(GetConnectionLock).ToArray();
        foreach (var connectionLock in locks)
        {
            await connectionLock.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            await AppendConnectionAsync(sourceNode.Name, targetNode.Name).ConfigureAwait(false);
            await AppendConnectionAsync(targetNode.Name, sourceNode.Name).ConfigureAwait(false);
        }
        finally
        {
            foreach (var connectionLock in locks.Reverse())
            {
                connectionLock.Release();
            }
        }
    }

    public async Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var connections = await ReadConnectionsWithLockAsync(node.Name).ConfigureAwait(false);
        var nodes = new List<Node>();
        foreach (var connection in connections)
        {
            var document = await ReadMetadataWithLockAsync(connection).ConfigureAwait(false);
            if (document is not null)
            {
                nodes.Add(CreateNode(document));
            }
        }

        return nodes;
    }

    public async Task<IReadOnlyCollection<Node>> GetAllNodesAsync()
    {
        if (!Directory.Exists(_metadataRoot))
        {
            return Array.Empty<Node>();
        }

        var nodes = new List<Node>();
        foreach (var metadataPath in Directory.EnumerateFiles(_metadataRoot, $"*{_options.MetadataFileExtension}", SearchOption.TopDirectoryOnly))
        {
            var encodedName = Path.GetFileNameWithoutExtension(metadataPath);
            if (string.IsNullOrWhiteSpace(encodedName))
            {
                continue;
            }

            var nodeName = DecodeNodeName(encodedName);
            var document = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
            if (document is not null)
            {
                nodes.Add(CreateNode(document));
            }
        }

        return nodes;
    }

    public async Task<Subgraph> GetSubgraphAsync(SubgraphQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.RootNodeIds.Count == 0)
        {
            return Subgraph.Empty;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discovered = new HashSet<string>(query.RootNodeIds, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string NodeName, int Depth)>();
        var documents = new Dictionary<string, NodeDocument>(StringComparer.OrdinalIgnoreCase);
        var connectionMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in query.RootNodeIds)
        {
            queue.Enqueue((root, 0));
        }

        while (queue.Count > 0)
        {
            var (nodeName, depth) = queue.Dequeue();
            if (!visited.Add(nodeName))
            {
                continue;
            }

            var document = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            documents[nodeName] = document;
            var connections = await ReadConnectionsWithLockAsync(nodeName).ConfigureAwait(false);
            var relevantConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var connection in connections)
            {
                if (depth < query.MaxDepth && discovered.Add(connection))
                {
                    queue.Enqueue((connection, depth + 1));
                }

                if (discovered.Contains(connection))
                {
                    relevantConnections.Add(connection);
                }
            }

            connectionMap[nodeName] = relevantConnections;
        }

        if (documents.Count == 0)
        {
            return Subgraph.Empty;
        }

        var nodes = documents.ToDictionary(
            static x => x.Key,
            x => CreateNode(x.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (nodeName, connections) in connectionMap)
        {
            if (!nodes.TryGetValue(nodeName, out var node))
            {
                continue;
            }

            node.SetConnections(connections.Where(nodes.ContainsKey).Select(x => nodes[x]).ToArray());
        }

        return new Subgraph { Nodes = nodes.Values.ToArray() };
    }

    private async Task<NodeDocument?> ReadMetadataWithLockAsync(string nodeName)
    {
        var metadataLock = GetMetadataLock(nodeName);
        await metadataLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadMetadataAsync(nodeName).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<string>> ReadConnectionsWithLockAsync(string nodeName)
    {
        var connectionLock = GetConnectionLock(nodeName);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadConnectionsAsync(nodeName).ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task<NodeDocument?> ReadMetadataAsync(string nodeName)
    {
        var metadataPath = GetMetadataPath(nodeName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var document = await JsonSerializer.DeserializeAsync<NodeDocument>(stream, SerializerOptions).ConfigureAwait(false);
        return document is null
            ? null
            : document with { Attributes = CopyAttributes(document.Attributes) };
    }

    private async Task WriteMetadataAsync(NodeDocument document)
    {
        var metadataPath = GetMetadataPath(document.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await using var stream = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions).ConfigureAwait(false);
    }

    private async Task EnsureConnectionsFileAsync(string nodeName)
    {
        var path = GetConnectionsPath(nodeName);
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, Array.Empty<string>(), SerializerOptions).ConfigureAwait(false);
    }

    private async Task AppendConnectionAsync(string sourceNodeName, string targetNodeName)
    {
        var connections = await ReadConnectionsAsync(sourceNodeName).ConfigureAwait(false);
        if (connections.Contains(targetNodeName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var updated = new HashSet<string>(connections, StringComparer.OrdinalIgnoreCase) { targetNodeName };
        await WriteConnectionsAsync(sourceNodeName, updated).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<string>> ReadConnectionsAsync(string nodeName)
    {
        var path = GetConnectionsPath(nodeName);
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var connections = await JsonSerializer.DeserializeAsync<HashSet<string>>(stream, SerializerOptions).ConfigureAwait(false);
        return connections is null
            ? Array.Empty<string>()
            : connections.ToArray();
    }

    private async Task WriteConnectionsAsync(string nodeName, IReadOnlyCollection<string> connections)
    {
        var path = GetConnectionsPath(nodeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, connections, SerializerOptions).ConfigureAwait(false);
    }

    private async Task EnsureNodeExistsAsync(string nodeName)
    {
        var metadata = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new DirectoryNotFoundException($"Node '{nodeName}' was not found.");
        }
    }

    private SemaphoreSlim GetMetadataLock(string nodeName)
    {
        return _metadataLocks.GetOrAdd(nodeName, static _ => new SemaphoreSlim(1, 1));
    }

    private SemaphoreSlim GetConnectionLock(string nodeName)
    {
        return _connectionLocks.GetOrAdd(nodeName, static _ => new SemaphoreSlim(1, 1));
    }

    private string GetMetadataPath(string nodeName)
    {
        return Path.Combine(_metadataRoot, EncodeNodeName(nodeName) + _options.MetadataFileExtension);
    }

    private string GetConnectionsPath(string nodeName)
    {
        return Path.Combine(_connectionsRoot, EncodeNodeName(nodeName) + _options.ConnectionsFileExtension);
    }

    private static StoredNode CreateNode(NodeDocument document)
    {
        return new StoredNode(document.Name) { AttributesSnapshot = CopyAttributes(document.Attributes) };
    }

    private static Dictionary<string, string> CopyAttributes(IDictionary<string, string>? attributes)
    {
        return attributes is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetNodeName(Node? parent, string subNodeName)
    {
        return parent is null
            ? subNodeName
            : $"{parent.Name}/{subNodeName}";
    }

    private static IEnumerable<string> Order(string first, string second)
    {
        return string.Compare(first, second, StringComparison.OrdinalIgnoreCase) < 0
            ? new[] { first, second }
            : new[] { second, first };
    }

    private static string EncodeNodeName(string nodeName)
    {
        var bytes = Encoding.UTF8.GetBytes(nodeName);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string DecodeNodeName(string encodedName)
    {
        var bytes = new byte[encodedName.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(encodedName.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record NodeDocument(string Name, Dictionary<string, string> Attributes);

    private sealed record StoredNode(string NodeName) : Node
    {
        private IReadOnlyDictionary<string, Edge>? _edges;
        private IReadOnlyCollection<Node> _nodes = Array.Empty<Node>();

        public override string Name => NodeName;

        public override IReadOnlyDictionary<string, Edge> Edges => _edges ??= _nodes.ToDictionary(
            static x => x.Name,
            x => (Edge)new StoredEdge(this, x),
            StringComparer.OrdinalIgnoreCase);

        public override IReadOnlyCollection<Node> Nodes => _nodes;

        public override IReadOnlyDictionary<string, string> Attributes => AttributesSnapshot;

        internal IReadOnlyDictionary<string, string> AttributesSnapshot { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal void SetConnections(IReadOnlyCollection<Node> nodes)
        {
            _nodes = nodes;
            _edges = null;
        }

        public bool Equals(StoredNode? other)
        {
            return other is not null && string.Equals(NodeName, other.NodeName, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(NodeName);
        }
    }

    private sealed record StoredEdge(Node First, Node Second) : Edge
    {
        public override Node Node1 => First;

        public override Node Node2 => Second;
    }
}
