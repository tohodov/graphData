using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<ServiceResult<NodeState>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null);
    Task<ServiceResult<NodeState>> Get(NodeGlobalId path);

    NodeGlobalId DeserializeGlobalId(string value) {
        var decoded = Uri.UnescapeDataString(value);
        var segments = decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment);
        return new NodeGlobalId(segments);
    }

    async Task<ServiceResult<NodeState>> GetNeighbor(NodeGlobalId globalId, NodeLocalId localId) {
        if (!NodeNameValidator.TryValidateSegment(localId, "Neighbor LocalId", out var validationError))
            return ServiceResult<NodeState>.BadRequest(validationError);

        var result = await Get(globalId);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult<NodeState>.From(result);

        var node = result.Value;
        var matches = node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId && neighbor.LocalId == localId)
            .DistinctBy(static neighbor => neighbor.GlobalId)
            .Take(2)
            .ToArray();

        return matches.Length switch {
            0 => ServiceResult<NodeState>.NotFound(),
            1 => ServiceResult<NodeState>.Ok(matches[0]),
            _ => ServiceResult<NodeState>.Conflict($"More than one neighbor with LocalId '{localId}' was found for node '{globalId}'.")
        };
    }

    async Task<ServiceResult> Update(NodeGlobalId path, IDictionary<string, string> attributes) {
        var result = await Get(path);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult.From(result);

        try {
            result.Value.Attributes = attributes.ToDictionary();
        } catch (Exception ex) {
            return ServiceResult.InternalServerError(ex.ToString());
        }

        return ServiceResult.Ok();
    }

    Task<ServiceResult> Delete(NodeGlobalId path);
    Task<ServiceResult> Connect(NodeGlobalId sourcePath, NodeGlobalId targetPath);
    Task<ServiceResult> Disconnect(NodeGlobalId sourcePath, NodeGlobalId targetPath);
    Task<ServiceResult<IReadOnlyCollection<NodeState>>> GetConnectedNodesAsync(NodeState node);
}
