using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.PerNodeFileStorage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.PerNodeFileStorage;

[Obsolete("пока SymLinkStorage основной", true)]
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
            throw new ArgumentException("Root path must be provided.", nameof(options));

        _options.RootPath = Path.GetFullPath(_options.RootPath);
        _metadataRoot = Path.Combine(_options.RootPath, _options.MetadataDirectoryName);
        _connectionsRoot = Path.Combine(_options.RootPath, _options.ConnectionsDirectoryName);

        Directory.CreateDirectory(_metadataRoot);
        Directory.CreateDirectory(_connectionsRoot);
    }

    public async Task<ServiceResult<Node>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null)
    {
        if (!NodeNameValidator.TryValidateSegment(name, "Node name", out var validationError))
            return ServiceResult<Node>.BadRequest(validationError);

        var parentNode = parent != null
            ? await FindNodeAsync(parent.Value).ConfigureAwait(false)
            : null;
        if (parent != null && parentNode is null)
            return ServiceResult<Node>.NotFound($"Parent node '{parent}' was not found.");

        var created = await CreateNodeAsync(name, parentNode, attributes).ConfigureAwait(false);
        return ServiceResult<Node>.Ok(created);
    }

    public async Task<ServiceResult<Node>> Get(NodeGlobalId path)
    {
        var node = await FindNodeAsync(path).ConfigureAwait(false);
        return node is null
            ? ServiceResult<Node>.NotFound()
            : ServiceResult<Node>.Ok(node);
    }

    public async Task<ServiceResult> Delete(NodeGlobalId path)
    {
        var node = await FindNodeAsync(path).ConfigureAwait(false) as StoredNode;
        if (node == null)
            return ServiceResult.NotFound();
        var nodeName = node.NodeName;
        var existingConnections = await ReadConnectionsWithLockAsync(nodeName).ConfigureAwait(false);
        var metadataLock = GetMetadataLock(nodeName);
        await metadataLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var metadataPath = GetMetadataPath(nodeName);
            if (!File.Exists(metadataPath))
                return ServiceResult.NotFound();
            File.Delete(metadataPath);
        }
        finally
        {
            metadataLock.Release();
        }

        var connectionLock = GetConnectionLock(nodeName);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connectionsPath = GetConnectionsPath(nodeName);
            if (File.Exists(connectionsPath))
            {
                File.Delete(connectionsPath);
            }
        }
        finally
        {
            connectionLock.Release();
        }

        foreach (var connection in existingConnections)
        {
            await RemoveConnectionAsync(connection, nodeName).ConfigureAwait(false);
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> Connect(NodeGlobalId sourcePath, NodeGlobalId targetPath)
    {
        if (sourcePath.SequenceEqual(targetPath))
            return ServiceResult.BadRequest("SourcePath and TargetPath must be different.");

        var sourceNode = await FindNodeAsync(sourcePath).ConfigureAwait(false);
        var targetNode = await FindNodeAsync(targetPath).ConfigureAwait(false);
        if (sourceNode is null || targetNode is null)
            return ServiceResult.NotFound();

        try
        {
            await ConnectNodesAsync(sourceNode, targetNode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ServiceResult.InternalServerError(ex.ToString());
        }

        return ServiceResult.Ok();
    }

    public Task<ServiceResult> Disconnect(NodeGlobalId sourcePath, NodeGlobalId targetPath) =>
        Task.FromResult(ServiceResult.InternalServerError(new NotImplementedException().ToString()));

    public async Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(Node node)
    {
        if (await ReadMetadataWithLockAsync(node.LocalId).ConfigureAwait(false) is null)
            return ServiceResult<IReadOnlyCollection<Node>>.NotFound();

        var nodes = await GetConnectedNodesCoreAsync(node).ConfigureAwait(false);
        return ServiceResult<IReadOnlyCollection<Node>>.Ok(nodes);
    }

    private async Task<Node> CreateNodeAsync(string name, Node? parent = null, IDictionary<string, string>? attributes = null)
    {
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

    private async Task<Node?> FindNodeAsync(Node? parent, string subNodeName)
    {
        var nodeName = GetNodeName(parent, subNodeName);
        var document = await ReadMetadataWithLockAsync(nodeName).ConfigureAwait(false);
        return document is null ? null : CreateNode(document);
    }

    private async Task<Node?> FindNodeAsync(NodeGlobalId path)
    {
        Node? node = null;
        foreach (var part in path)
        {
            node = await FindNodeAsync(node, part).ConfigureAwait(false);
            if (node is null)
                return null;
        }

        return node;
    }

    private async Task ConnectNodesAsync(Node sourceNode, Node targetNode)
    {
        var sourceNodeName = sourceNode.LocalId;
        var targetNodeName = targetNode.LocalId;

        if (string.Equals(sourceNodeName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            return;

        await EnsureNodeExistsAsync(sourceNodeName).ConfigureAwait(false);
        await EnsureNodeExistsAsync(targetNodeName).ConfigureAwait(false);

        var orderedNames = Order(sourceNodeName, targetNodeName).ToArray();
        var locks = orderedNames.Select(GetConnectionLock).ToArray();
        foreach (var connectionLock in locks)
        {
            await connectionLock.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            await AppendConnectionAsync(sourceNodeName, targetNodeName).ConfigureAwait(false);
            await AppendConnectionAsync(targetNodeName, sourceNodeName).ConfigureAwait(false);
        }
        finally
        {
            foreach (var connectionLock in locks.Reverse())
            {
                connectionLock.Release();
            }
        }
    }

    private async Task<IReadOnlyCollection<Node>> GetConnectedNodesCoreAsync(Node node)
    {
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

    private async Task RemoveConnectionAsync(string sourceNodeName, string targetNodeName)
    {
        var connectionLock = GetConnectionLock(sourceNodeName);
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsAsync(sourceNodeName).ConfigureAwait(false);
            var updated = new HashSet<string>(connections, StringComparer.OrdinalIgnoreCase);
            if (updated.Remove(targetNodeName))
            {
                await WriteConnectionsAsync(sourceNodeName, updated).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLock.Release();
        }
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
            : $"{parent.LocalId}/{subNodeName}";
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

    private sealed class StoredNode(string nodeName) : Node
    {
        private ICollection<Edge>? _edges;
        private ICollection<Node> _nodes = Array.Empty<Node>();

        public string NodeName { get; } = nodeName;

        public override NodeLocalId LocalId => new(NodeName);
        public override NodeGlobalId GlobalId => throw new NotImplementedException();

        public override ICollection<Edge> Edges => _edges ??= _nodes.Select(x => (Edge)new StoredEdge(this, x)).ToArray();

        public override ICollection<Node> Nodes => _nodes;

        public override IDictionary<string, string> Attributes { get => AttributesSnapshot; set => throw new NotImplementedException(); } //TODO

        internal IDictionary<string, string> AttributesSnapshot { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal void SetConnections(IReadOnlyCollection<Node> nodes)
        {
            _nodes = nodes.ToArray();
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

    private sealed class StoredEdge(Node First, Node Second) : Edge
    {
        public override Node Node1 => First;

        public override Node Node2 => Second;
    }
}
