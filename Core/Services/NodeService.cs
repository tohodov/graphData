using System;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class NodeService(IGraphStorage storage) : INodeService
{
    private readonly IGraphStorage _storage = storage;

    public async Task<NodeDetails?> GetNodeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var metadata = await _storage.GetNodeMetadataAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var connections = await _storage.GetConnectedNodesAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return new NodeDetails
        {
            Metadata = metadata,
            Connections = connections
        };
    }

    public async Task<NodeMetadata> CreateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        var nodeId = metadata.Id == Guid.Empty ? Guid.NewGuid() : metadata.Id;
        var normalized = metadata with { Id = nodeId };
        return await _storage.CreateNodeAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Id == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(metadata));
        }

        return _storage.UpdateMetadataAsync(metadata, cancellationToken);
    }

    public Task ConnectNodesAsync(Guid firstNodeId, Guid secondNodeId, CancellationToken cancellationToken = default)
    {
        if (firstNodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(firstNodeId));
        }

        if (secondNodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id must be provided.", nameof(secondNodeId));
        }

        return _storage.ConnectNodesAsync(firstNodeId, secondNodeId, cancellationToken);
    }

    public Task<Subgraph> GetSubgraphAsync(SubgraphQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.RootNodeIds.Count == 0)
        {
            return Task.FromResult(Subgraph.Empty);
        }

        if (query.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.MaxDepth), "Depth must be non-negative.");
        }

        return _storage.GetSubgraphAsync(query, cancellationToken);
    }
}
