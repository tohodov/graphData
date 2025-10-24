using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.NtfsStorage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphData.NtfsStorage;

public sealed class SymLinkGraphStorage : IGraphStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly NtfsGraphStorageOptions _options;
    private readonly ILogger<SymLinkGraphStorage> _logger;

    public SymLinkGraphStorage(IOptions<NtfsGraphStorageOptions> options, ILogger<SymLinkGraphStorage> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _options.RootPath = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_options.RootPath);
    }

    public async Task<NodeMetadata> CreateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Id == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }
        EnsureAttributes(metadata);

        var nodePath = GetNodePath(metadata.Id);
        Directory.CreateDirectory(nodePath);
        await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<NodeMetadata?> GetNodeMetadataAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetMetadataPath(nodeId);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var metadata = await JsonSerializer.DeserializeAsync<NodeMetadata>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        EnsureAttributes(metadata);
        return metadata;
    }

    public async Task UpdateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Id == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }
        EnsureAttributes(metadata);

        var nodePath = GetNodePath(metadata.Id);
        if (!Directory.Exists(nodePath))
        {
            throw new DirectoryNotFoundException($"Node directory '{nodePath}' was not found.");
        }

        await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
    }

    public Task ConnectNodesAsync(Guid sourceNodeId, Guid targetNodeId, CancellationToken cancellationToken = default)
    {
        if (sourceNodeId == targetNodeId)
        {
            return Task.CompletedTask;
        }

        var sourcePath = GetNodePath(sourceNodeId);
        var targetPath = GetNodePath(targetNodeId);

        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Node directory '{sourcePath}' was not found.");
        }

        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException($"Node directory '{targetPath}' was not found.");
        }

        CreateLinkIfMissing(sourcePath, targetPath, targetNodeId);
        CreateLinkIfMissing(targetPath, sourcePath, sourceNodeId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<Guid>> GetConnectedNodesAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var nodePath = GetNodePath(nodeId);
        if (!Directory.Exists(nodePath))
        {
            return Task.FromResult<IReadOnlyCollection<Guid>>(Array.Empty<Guid>());
        }

        var directory = new DirectoryInfo(nodePath);
        var connections = new HashSet<Guid>();

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, _options.MetadataFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Guid.TryParse(entry.Name, out var connectedId))
            {
                connections.Add(connectedId);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Guid>>(connections.ToArray());
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

    private string GetNodePath(Guid nodeId)
    {
        return Path.Combine(_options.RootPath, nodeId.ToString("D"));
    }

    private string GetMetadataPath(Guid nodeId)
    {
        return Path.Combine(GetNodePath(nodeId), _options.MetadataFileName);
    }

    private async Task WriteMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(metadata.Id);
        await using var stream = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, metadata, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private void CreateLinkIfMissing(string sourcePath, string targetPath, Guid targetId)
    {
        var linkPath = Path.Combine(sourcePath, targetId.ToString("D"));
        try
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                return;
            }

            var targetFullPath = Path.GetFullPath(targetPath);
            Directory.CreateSymbolicLink(linkPath, targetFullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create link from {Source} to {Target}", sourcePath, targetPath);
            throw;
        }
    }

    private static void EnsureAttributes(NodeMetadata metadata)
    {
        if (metadata.Attributes is null)
        {
            metadata.Attributes = new Dictionary<string, string>();
        }
    }
}
