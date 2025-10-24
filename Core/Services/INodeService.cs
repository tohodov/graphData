using GraphData.Core.Models;

namespace GraphData.Core.Services;

public interface INodeService
{
    Task<NodeDetails?> GetNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);

    Task<NodeMetadata> CreateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

    Task UpdateNodeAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

    Task ConnectNodesAsync(Guid firstNodeId, Guid secondNodeId, CancellationToken cancellationToken = default);
}
