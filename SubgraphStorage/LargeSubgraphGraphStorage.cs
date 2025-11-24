using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.SubgraphStorage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.SubgraphStorage;

public sealed class LargeSubgraphGraphStorage : IGraphStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LargeSubgraphGraphStorageOptions _options;
    private readonly ILogger<LargeSubgraphGraphStorage> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionsLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _metadataRoot;
    private readonly string _connectionsRoot;

    public LargeSubgraphGraphStorage(IOptions<LargeSubgraphGraphStorageOptions> options, ILogger<LargeSubgraphGraphStorage> logger)
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

    public async Task<Node> CreateNodeAsync(Node metadata, CancellationToken cancellationToken = default)
    {
        var bucketKey = GetBucketKey(metadata.Name);
        var bucketLock = GetMetadataLock(bucketKey);
        await bucketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(bucketKey, cancellationToken).ConfigureAwait(false);
            nodes[metadata.Name] = metadata;
            await WriteMetadataBucketAsync(bucketKey, nodes, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Stored metadata for node {NodeId} in bucket {Bucket}", metadata.Name, bucketKey);
        }
        finally
        {
            bucketLock.Release();
        }

        await EnsureConnectionBucketEntryAsync(metadata.Name, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<Node?> GetNodeMetadataAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var bucketKey = GetBucketKey(nodeId);
        var bucketLock = GetMetadataLock(bucketKey);
        await bucketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(bucketKey, cancellationToken).ConfigureAwait(false);
            return nodes.TryGetValue(nodeId, out var metadata)
                ? metadata
                : null;
        }
        finally
        {
            bucketLock.Release();
        }
    }

    public async Task UpdateMetadataAsync(Node metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Name == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }

        var bucketKey = GetBucketKey(metadata.Name);
        var bucketLock = GetMetadataLock(bucketKey);
        await bucketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nodes = await ReadMetadataBucketAsync(bucketKey, cancellationToken).ConfigureAwait(false);
            if (!nodes.ContainsKey(metadata.Name))
            {
                throw new KeyNotFoundException($"Metadata for node '{metadata.Name}' was not found.");
            }

            nodes[metadata.Name] = metadata;
            await WriteMetadataBucketAsync(bucketKey, nodes, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Updated metadata for node {NodeId} in bucket {Bucket}", metadata.Name, bucketKey);
        }
        finally
        {
            bucketLock.Release();
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

        var firstKey = GetBucketKey(sourceNodeId);
        var secondKey = GetBucketKey(targetNodeId);
        var orderedKeys = OrderKeys(firstKey, secondKey);

        var locks = orderedKeys.Select(GetConnectionsLock).ToArray();
        foreach (var connectionLock in locks)
        {
            await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var firstConnections = await ReadConnectionsBucketAsync(firstKey, cancellationToken).ConfigureAwait(false);
            var secondConnections = firstKey == secondKey
                ? firstConnections
                : await ReadConnectionsBucketAsync(secondKey, cancellationToken).ConfigureAwait(false);

            var firstSet = GetOrCreateConnectionSet(firstConnections, sourceNodeId);
            var secondSet = GetOrCreateConnectionSet(secondConnections, targetNodeId);

            var addedToFirst = firstSet.Add(targetNodeId);
            var addedToSecond = secondSet.Add(sourceNodeId);

            if (addedToFirst)
            {
                firstConnections[sourceNodeId] = firstSet;
                await WriteConnectionsBucketAsync(firstKey, firstConnections, cancellationToken).ConfigureAwait(false);
            }

            if (addedToSecond)
            {
                secondConnections[targetNodeId] = secondSet;
                if (secondKey != firstKey)
                {
                    await WriteConnectionsBucketAsync(secondKey, secondConnections, cancellationToken).ConfigureAwait(false);
                }
                else if (!addedToFirst)
                {
                    await WriteConnectionsBucketAsync(firstKey, firstConnections, cancellationToken).ConfigureAwait(false);
                }
            }

            if (addedToFirst || addedToSecond)
            {
                _logger.LogDebug("Connected {Source} <-> {Target}", sourceNodeId, targetNodeId);
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

    public async Task<IReadOnlyCollection<Guid>> GetConnectedNodesAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var bucketKey = GetBucketKey(nodeId);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey, cancellationToken).ConfigureAwait(false);
            return connections.TryGetValue(nodeId, out var set)
                ? set.ToArray()
                : Array.Empty<Guid>();
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

        foreach (var root in query.RootNodeIds)
        {
            queue.Enqueue((root, 0));
        }

        var nodes = new Dictionary<Guid, NodeDetails>();

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (nodeId, depth) = queue.Dequeue();
            if (!visited.Add(nodeId))
            {
                continue;
            }

            var metadata = await GetNodeMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                continue;
            }

            var connections = await GetConnectedNodesAsync(nodeId, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureNodeExistsAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var metadata = await GetNodeMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new DirectoryNotFoundException($"Node '{nodeId}' does not exist in storage.");
        }
    }

    private async Task EnsureConnectionBucketEntryAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var bucketKey = GetBucketKey(nodeId);
        var connectionLock = GetConnectionsLock(bucketKey);
        await connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connections = await ReadConnectionsBucketAsync(bucketKey, cancellationToken).ConfigureAwait(false);
            if (!connections.ContainsKey(nodeId))
            {
                connections[nodeId] = new HashSet<Guid>();
                await WriteConnectionsBucketAsync(bucketKey, connections, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task<Dictionary<Guid, Node>> ReadMetadataBucketAsync(string bucketKey, CancellationToken cancellationToken)
    {
        var path = GetMetadataBucketPath(bucketKey);
        if (!File.Exists(path))
        {
            return new Dictionary<Guid, Node>();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        return await JsonSerializer.DeserializeAsync<Dictionary<Guid, Node>>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new Dictionary<Guid, Node>();
    }

    private async Task WriteMetadataBucketAsync(string bucketKey, Dictionary<Guid, Node> nodes, CancellationToken cancellationToken)
    {
        var path = GetMetadataBucketPath(bucketKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, nodes, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<Guid, HashSet<Guid>>> ReadConnectionsBucketAsync(string bucketKey, CancellationToken cancellationToken)
    {
        var path = GetConnectionsBucketPath(bucketKey);
        if (!File.Exists(path))
        {
            return new Dictionary<Guid, HashSet<Guid>>();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        return await JsonSerializer.DeserializeAsync<Dictionary<Guid, HashSet<Guid>>>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new Dictionary<Guid, HashSet<Guid>>();
    }

    private async Task WriteConnectionsBucketAsync(string bucketKey, Dictionary<Guid, HashSet<Guid>> connections, CancellationToken cancellationToken)
    {
        var path = GetConnectionsBucketPath(bucketKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, connections, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private SemaphoreSlim GetMetadataLock(string bucketKey)
    {
        return _metadataLocks.GetOrAdd(bucketKey, static _ => new SemaphoreSlim(1, 1));
    }

    private SemaphoreSlim GetConnectionsLock(string bucketKey)
    {
        return _connectionsLocks.GetOrAdd(bucketKey, static _ => new SemaphoreSlim(1, 1));
    }

    private string GetBucketKey(Guid nodeId)
    {
        var normalized = nodeId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
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

    private static HashSet<Guid> GetOrCreateConnectionSet(Dictionary<Guid, HashSet<Guid>> map, Guid nodeId)
    {
        if (!map.TryGetValue(nodeId, out var set))
        {
            set = new HashSet<Guid>();
            map[nodeId] = set;
        }

        return set;
    }

    private static IReadOnlyList<string> OrderKeys(string firstKey, string secondKey)
    {
        if (string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { firstKey };
        }

        return string.Compare(firstKey, secondKey, StringComparison.OrdinalIgnoreCase) < 0
            ? new[] { firstKey, secondKey }
            : new[] { secondKey, firstKey };
    }
}
