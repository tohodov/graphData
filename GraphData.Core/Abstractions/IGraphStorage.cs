using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<NodeMetadata> CreateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

    Task<NodeMetadata?> GetNodeMetadataAsync(Guid nodeId, CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

    Task ConnectNodesAsync(Guid sourceNodeId, Guid targetNodeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Guid>> GetConnectedNodesAsync(Guid nodeId, CancellationToken cancellationToken = default);
}
