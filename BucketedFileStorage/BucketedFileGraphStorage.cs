using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphData.BucketedFileStorage.Options;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.BucketedFileStorage;

public sealed class BucketedFileGraphStorage : IGraphStorage, IGraphNodeCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly BucketedFileGraphStorageOptions _options;
    private readonly ILogger<BucketedFileGraphStorage> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionsLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _metadataRoot;
    private readonly string _connectionsRoot;

    public BucketedFileGraphStorage(IOptions<BucketedFileGraphStorageOptions> options, ILogger<BucketedFileGraphStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.RootPath))
            throw new ArgumentException("Root path must be provided.", nameof(options));

        _options.RootPath = Path.GetFullPath(_options.RootPath);
        _metadataRoot = Path.Combine(_options.RootPath, _options.MetadataDirectoryName);
        _connectionsRoot = Path.Combine(_options.RootPath, _options.ConnectionsDirectoryName);

        Directory.CreateDirectory(_metadataRoot);
        Directory.CreateDirectory(_connectionsRoot);
    }

    public async Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null)
    {
        var nodeName = GetNodeName(parent, name);
        var bucketKey = GetBucketKey(nodeName);
        var bucketLock = GetMetadataLock(bucketKey);
        var document = new NodeDocument(nodeName, CopyAttributes(attributes));

        await bucketLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(bucketKey).ConfigureAwait(false);
            nodes[nodeName] = document;
            await WriteMetadataBucketAsync(bucketKey, nodes).ConfigureAwait(false);
            _logger.LogDebug("Stored node {NodeName} in metadata bucket {Bucket}.", nodeName, bucketKey);
        }
        finally
        {
            bucketLock.Release();
        }

        await EnsureConnectionBucketEntryAsync(nodeName).ConfigureAwait(false);
        return CreateNode(document);
    }

    public async Task<Node?> Get(Node? parent, string subNodeName)
    {
        var nodeName = GetNodeName(parent, subNodeName);
        var document = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
        return document is null ? null : CreateNode(document);
    }

    public async Task<Node?> Get(NodePath query)
    {
        Node? node = null;
        foreach (var part in query)
        {
            node = await Get(node, part).ConfigureAwait(false);
            if (node is null)
                return null;
        }
        return node;
    }

    public async Task Delete(NodePath path)
    {
        var node = await Get(path) as StoredNode;
        if (node == null)
            return;
        var nodeName = node.NodeName;
        var existingConnections = await ReadConnectionsWithLockAsync(nodeName).ConfigureAwait(false);
        var metadataBucketKey = GetBucketKey(nodeName);
        var metadataLock = GetMetadataLock(metadataBucketKey);
        await metadataLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(metadataBucketKey).ConfigureAwait(false);
            if (!nodes.Remove(nodeName))
                return;

            await WriteMetadataBucketAsync(metadataBucketKey, nodes).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }

        await RemoveConnectionEntryAsync(nodeName).ConfigureAwait(false);
        foreach (var connection in existingConnections)
            await RemoveConnectionAsync(connection, nodeName).ConfigureAwait(false);
    }

    public async Task Connect(Node sourceNode, Node targetNode)
    {
        ArgumentNullException.ThrowIfNull(sourceNode);
        ArgumentNullException.ThrowIfNull(targetNode);

        if (string.Equals(sourceNode.LocalId, targetNode.LocalId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await EnsureNodeExistsAsync(sourceNode.LocalId).ConfigureAwait(false);
        await EnsureNodeExistsAsync(targetNode.LocalId).ConfigureAwait(false);

        var firstKey = GetBucketKey(sourceNode.LocalId);
        var secondKey = GetBucketKey(targetNode.LocalId);
        var locks = Order(firstKey, secondKey).Select(GetConnectionsLock).ToArray();

        foreach (var connectionLock in locks)
        {
            await connectionLock.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            var firstConnections = await ReadConnectionsBucketAsync(firstKey).ConfigureAwait(false);
            var secondConnections = firstKey == secondKey
                ? firstConnections
                : await ReadConnectionsBucketAsync(secondKey).ConfigureAwait(false);

            var firstSet = GetOrCreateConnectionSet(firstConnections, sourceNode.LocalId);
            var secondSet = GetOrCreateConnectionSet(secondConnections, targetNode.LocalId);

            var addedToFirst = firstSet.Add(targetNode.LocalId);
            var addedToSecond = secondSet.Add(sourceNode.LocalId);

            if (addedToFirst)
            {
                firstConnections[sourceNode.LocalId] = firstSet;
                await WriteConnectionsBucketAsync(firstKey, firstConnections).ConfigureAwait(false);
            }

            if (addedToSecond)
            {
                secondConnections[targetNode.LocalId] = secondSet;
                if (secondKey != firstKey)
                {
                    await WriteConnectionsBucketAsync(secondKey, secondConnections).ConfigureAwait(false);
                }
                else if (!addedToFirst)
                {
                    await WriteConnectionsBucketAsync(firstKey, firstConnections).ConfigureAwait(false);
                }
            }
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

        var connections = await ReadConnectionsWithLockAsync(node.LocalId).ConfigureAwait(false);
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
        foreach (var metadataPath in Directory.EnumerateFiles(_metadataRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var bucketKey = Path.GetFileNameWithoutExtension(metadataPath);
            if (string.IsNullOrWhiteSpace(bucketKey))
            {
                continue;
            }

            var bucketLock = GetMetadataLock(bucketKey);
            await bucketLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var bucket = await ReadMetadataBucketAsync(bucketKey).ConfigureAwait(false);
                nodes.AddRange(bucket.Values.Select(CreateNode));
            }
            finally
            {
                bucketLock.Release();
            }
        }

        return nodes;
    }

    private async Task<NodeDocument?> ReadMetadataWithLockAsync(string nodeName)
    {
        var bucketKey = GetBucketKey(nodeName);
        var bucketLock = GetMetadataLock(bucketKey);
        await bucketLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(bucketKey).ConfigureAwait(false);
            return nodes.TryGetValue(nodeName, out var document)
                ? document with { Attributes = CopyAttributes(document.Attributes) }
                : null;
        }
        finally
        {
            bucketLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<string>> ReadConnectionsWithLockAsync(string nodeName)
    {
        var bucketKey = GetBucketKey(nodeName);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey).ConfigureAwait(false);
            return connections.TryGetValue(nodeName, out var set)
                ? set.ToArray()
                : Array.Empty<string>();
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task EnsureNodeExistsAsync(string nodeName)
    {
        var metadata = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new DirectoryNotFoundException($"Node '{nodeName}' does not exist in storage.");
        }
    }

    private async Task EnsureConnectionBucketEntryAsync(string nodeName)
    {
        var bucketKey = GetBucketKey(nodeName);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey).ConfigureAwait(false);
            if (!connections.ContainsKey(nodeName))
            {
                connections[nodeName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await WriteConnectionsBucketAsync(bucketKey, connections).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task RemoveConnectionEntryAsync(string nodeName)
    {
        var bucketKey = GetBucketKey(nodeName);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey).ConfigureAwait(false);
            if (connections.Remove(nodeName))
            {
                await WriteConnectionsBucketAsync(bucketKey, connections).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task RemoveConnectionAsync(string sourceNodeName, string targetNodeName)
    {
        var bucketKey = GetBucketKey(sourceNodeName);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey).ConfigureAwait(false);
            if (connections.TryGetValue(sourceNodeName, out var set) && set.Remove(targetNodeName))
            {
                connections[sourceNodeName] = set;
                await WriteConnectionsBucketAsync(bucketKey, connections).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task<Dictionary<string, NodeDocument>> ReadMetadataBucketAsync(string bucketKey)
    {
        var path = GetMetadataBucketPath(bucketKey);
        if (!File.Exists(path))
        {
            return new Dictionary<string, NodeDocument>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var nodes = await JsonSerializer.DeserializeAsync<Dictionary<string, NodeDocument>>(stream, SerializerOptions).ConfigureAwait(false);
        return nodes is null
            ? new Dictionary<string, NodeDocument>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, NodeDocument>(nodes, StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteMetadataBucketAsync(string bucketKey, Dictionary<string, NodeDocument> nodes)
    {
        var path = GetMetadataBucketPath(bucketKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, nodes, SerializerOptions).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, HashSet<string>>> ReadConnectionsBucketAsync(string bucketKey)
    {
        var path = GetConnectionsBucketPath(bucketKey);
        if (!File.Exists(path))
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var connections = await JsonSerializer.DeserializeAsync<Dictionary<string, HashSet<string>>>(stream, SerializerOptions).ConfigureAwait(false);
        return connections is null
            ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            : connections.ToDictionary(
                static x => x.Key,
                static x => new HashSet<string>(x.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteConnectionsBucketAsync(string bucketKey, Dictionary<string, HashSet<string>> connections)
    {
        var path = GetConnectionsBucketPath(bucketKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, connections, SerializerOptions).ConfigureAwait(false);
    }

    private SemaphoreSlim GetMetadataLock(string bucketKey)
    {
        return _metadataLocks.GetOrAdd(bucketKey, static _ => new SemaphoreSlim(1, 1));
    }

    private SemaphoreSlim GetConnectionsLock(string bucketKey)
    {
        return _connectionsLocks.GetOrAdd(bucketKey, static _ => new SemaphoreSlim(1, 1));
    }

    private string GetBucketKey(string nodeName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nodeName));
        var normalized = Convert.ToHexString(hash).ToLowerInvariant();
        var prefixLength = Math.Clamp(_options.BucketPrefixLength, 1, normalized.Length);
        return normalized[..prefixLength];
    }

    private string GetMetadataBucketPath(string bucketKey)
    {
        return Path.Combine(_metadataRoot, $"{bucketKey}.json");
    }

    private string GetConnectionsBucketPath(string bucketKey)
    {
        return Path.Combine(_connectionsRoot, $"{bucketKey}.json");
    }

    private static HashSet<string> GetOrCreateConnectionSet(Dictionary<string, HashSet<string>> map, string nodeName)
    {
        if (!map.TryGetValue(nodeName, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[nodeName] = set;
        }

        return set;
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
            : $"{parent.LocalId}/{subNodeName}";
    }

    private static IReadOnlyList<string> Order(string firstKey, string secondKey)
    {
        if (string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { firstKey };
        }

        return string.Compare(firstKey, secondKey, StringComparison.OrdinalIgnoreCase) < 0
            ? new[] { firstKey, secondKey }
            : new[] { secondKey, firstKey };
    }

    private sealed record NodeDocument(string Name, Dictionary<string, string> Attributes);

    private sealed record StoredNode(string NodeName) : Node
    {
        private IReadOnlyDictionary<string, Edge>? _edges;
        private IReadOnlyCollection<Node> _nodes = Array.Empty<Node>();

        public override string LocalId => NodeName;
        public override NodePath GlobalId => throw new NotImplementedException(); //TODO подумать и реализовать

        public override IReadOnlyDictionary<string, Edge> Edges => _edges ??= _nodes.ToDictionary(
            static x => x.LocalId,
            x => (Edge)new StoredEdge(this, x),
            StringComparer.OrdinalIgnoreCase);

        public override IReadOnlyCollection<Node> Nodes => _nodes;

        public override IReadOnlyDictionary<string, string> Attributes { get => AttributesSnapshot; set => throw new NotImplementedException(); } //TODO

        internal IReadOnlyDictionary<string, string> AttributesSnapshot { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
