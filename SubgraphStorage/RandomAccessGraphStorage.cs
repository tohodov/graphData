using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.SubgraphStorage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.SubgraphStorage;

public sealed class RandomAccessGraphStorage : IGraphStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly RandomAccessGraphStorageOptions _options;
    private readonly ILogger<RandomAccessGraphStorage> _logger;
    private readonly string _metadataRoot;
    private readonly string _connectionsRoot;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _metadataLocks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _connectionLocks = new();

    public RandomAccessGraphStorage(IOptions<RandomAccessGraphStorageOptions> options, ILogger<RandomAccessGraphStorage> logger)
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

    public async Task<NodeMetadata> CreateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Id == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }

        EnsureAttributes(metadata);

        var metadataLock = GetMetadataLock(metadata.Id);
        await metadataLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }

        var connectionLock = GetConnectionLock(metadata.Id);
        await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectionsFileAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }

        _logger.LogDebug("Stored metadata for node {NodeId}", metadata.Id);
        return metadata;
    }

    public async Task<NodeMetadata?> GetNodeMetadataAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var metadataLock = GetMetadataLock(nodeId);
        await metadataLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await ReadMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
            return metadata is null ? null : Clone(metadata);
        }
        finally
        {
            metadataLock.Release();
        }
    }

    public async Task UpdateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Id == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }

        EnsureAttributes(metadata);

        var metadataLock = GetMetadataLock(metadata.Id);
        await metadataLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(GetMetadataPath(metadata.Id)))
            {
                throw new FileNotFoundException($"Metadata for node '{metadata.Id}' does not exist.");
            }

            await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            metadataLock.Release();
        }
    }

    public async Task ConnectNodesAsync(Guid sourceNodeId, Guid targetNodeId, CancellationToken cancellationToken = default)
    {
        if (sourceNodeId == targetNodeId)
        {
            return;
        }

        await EnsureNodeExistsAsync(sourceNodeId, cancellationToken).ConfigureAwait(false);
        await EnsureNodeExistsAsync(targetNodeId, cancellationToken).ConfigureAwait(false);

        var ordered = Order(sourceNodeId, targetNodeId);
        var locks = ordered.Select(GetConnectionLock).ToArray();

        foreach (var connectionLock in locks)
        {
            await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await AppendConnectionAsync(sourceNodeId, targetNodeId, cancellationToken).ConfigureAwait(false);
            await AppendConnectionAsync(targetNodeId, sourceNodeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var connectionLock in locks.Reverse())
            {
                connectionLock.Release();
            }
        }
    }

    public async Task<IReadOnlyCollection<Guid>> GetConnectedNodesAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var connectionLock = GetConnectionLock(nodeId);
        await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadConnectionsAsync(nodeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<Subgraph> GetSubgraphAsync(SubgraphQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.RootNodeIds.Count == 0)
        {
            return Subgraph.Empty;
        }

        var visited = new HashSet<Guid>();
        var discovered = new HashSet<Guid>(query.RootNodeIds);
        var queue = new Queue<(Guid NodeId, int Depth)>();
        var nodes = new Dictionary<Guid, NodeDetails>();
        var metadataCache = new Dictionary<Guid, NodeMetadata?>();
        var connectionCache = new Dictionary<Guid, IReadOnlyCollection<Guid>>();

        foreach (var root in query.RootNodeIds)
        {
            queue.Enqueue((root, 0));
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (nodeId, depth) = queue.Dequeue();
            if (!visited.Add(nodeId))
            {
                continue;
            }

        var metadata = await GetOrReadMetadataAsync(nodeId, metadataCache, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            continue;
        }

            var connections = await GetOrReadConnectionsAsync(nodeId, connectionCache, cancellationToken).ConfigureAwait(false);
            var relevantConnections = new HashSet<Guid>();

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

            nodes[nodeId] = new NodeDetails
            {
                Metadata = metadata,
                Connections = relevantConnections.ToArray()
            };
        }

        return nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph(nodes);
    }

    private async Task<NodeMetadata?> GetOrReadMetadataAsync(Guid nodeId, IDictionary<Guid, NodeMetadata?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }

        var metadata = await GetNodeMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
        cache[nodeId] = metadata;
        return metadata;
    }

    private async Task<IReadOnlyCollection<Guid>> GetOrReadConnectionsAsync(Guid nodeId, IDictionary<Guid, IReadOnlyCollection<Guid>> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }

        var connections = await GetConnectedNodesAsync(nodeId, cancellationToken).ConfigureAwait(false);
        cache[nodeId] = connections;
        return connections;
    }

    private async Task<NodeMetadata?> ReadMetadataAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(nodeId);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var metadata = await JsonSerializer.DeserializeAsync<NodeMetadata>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (metadata is not null)
        {
            EnsureAttributes(metadata);
        }

        return metadata;
    }

    private async Task WriteMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(metadata.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await using var stream = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, metadata, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConnectionsFileAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var path = GetConnectionsPath(nodeId);
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, Array.Empty<Guid>(), SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendConnectionAsync(Guid sourceNodeId, Guid targetNodeId, CancellationToken cancellationToken)
    {
        var path = GetConnectionsPath(sourceNodeId);
        var connections = await ReadConnectionsAsync(sourceNodeId, cancellationToken).ConfigureAwait(false);
        if (connections.Contains(targetNodeId))
        {
            return;
        }

        var updated = new HashSet<Guid>(connections) { targetNodeId };
        await WriteConnectionsAsync(sourceNodeId, updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<Guid>> ReadConnectionsAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var path = GetConnectionsPath(nodeId);
        if (!File.Exists(path))
        {
            return Array.Empty<Guid>();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var connections = await JsonSerializer.DeserializeAsync<HashSet<Guid>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return connections is null
            ? Array.Empty<Guid>()
            : connections.ToArray();
    }

    private async Task WriteConnectionsAsync(Guid nodeId, IReadOnlyCollection<Guid> connections, CancellationToken cancellationToken)
    {
        var path = GetConnectionsPath(nodeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, connections, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNodeExistsAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var metadata = await GetNodeMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new DirectoryNotFoundException($"Node '{nodeId}' was not found.");
        }
    }

    private SemaphoreSlim GetMetadataLock(Guid nodeId)
    {
        return _metadataLocks.GetOrAdd(nodeId, static _ => new SemaphoreSlim(1, 1));
    }

    private SemaphoreSlim GetConnectionLock(Guid nodeId)
    {
        return _connectionLocks.GetOrAdd(nodeId, static _ => new SemaphoreSlim(1, 1));
    }

    private string GetMetadataPath(Guid nodeId)
    {
        return Path.Combine(_metadataRoot, nodeId.ToString("D") + _options.MetadataFileExtension);
    }

    private string GetConnectionsPath(Guid nodeId)
    {
        return Path.Combine(_connectionsRoot, nodeId.ToString("D") + _options.ConnectionsFileExtension);
    }

    private static IEnumerable<Guid> Order(Guid first, Guid second)
    {
        return first.CompareTo(second) < 0
            ? new[] { first, second }
            : new[] { second, first };
    }

    private static void EnsureAttributes(NodeMetadata metadata)
    {
        if (metadata.Attributes is null || metadata.Attributes.Count == 0)
        {
            metadata.Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }

        if (metadata.Attributes.Comparer != StringComparer.Ordinal)
        {
            metadata.Attributes = new Dictionary<string, string>(metadata.Attributes, StringComparer.Ordinal);
        }
    }

    private static NodeMetadata Clone(NodeMetadata metadata)
    {
        return metadata with
        {
            Attributes = new Dictionary<string, string>(metadata.Attributes, StringComparer.Ordinal)
        };
    }
}
