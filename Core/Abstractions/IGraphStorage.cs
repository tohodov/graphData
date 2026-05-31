using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<ServiceResult<Node>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null);
    Task<ServiceResult<Node>> Get(NodeGlobalId path);

    NodeGlobalId DeserializeGlobalId(string value) {
        var decoded = Uri.UnescapeDataString(value);
        var segments = decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment);
        return new NodeGlobalId(segments);
    }

    async Task<ServiceResult<Node>> GetNeighbor(NodeGlobalId globalId, NodeLocalId localId) {
        if (!NodeNameValidator.TryValidateSegment(localId, "Neighbor LocalId", out var validationError))
            return ServiceResult<Node>.BadRequest(validationError);

        var result = await Get(globalId);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ServiceResult<Node>.From(result);

        var node = result.Value;
        var matches = node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId && neighbor.LocalId == localId)
            .DistinctBy(static neighbor => neighbor.GlobalId)
            .Take(2)
            .ToArray();

        return matches.Length switch {
            0 => ServiceResult<Node>.NotFound(),
            1 => ServiceResult<Node>.Ok(matches[0]),
            _ => ServiceResult<Node>.Conflict($"More than one neighbor with LocalId '{localId}' was found for node '{globalId}'.")
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
    Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(Node node);

    async Task<ServiceResult<Subgraph>> GetSubgraphAsync(SubgraphQuery query) { //TODO переосмыслить
        if (query.Nodes.Count == 0)
            return ServiceResult<Subgraph>.Ok(new Subgraph { Nodes = [] });

        var visitedRequests = new HashSet<NodeGlobalId>();//TODO хэш тут надо проверить
        var visitedNodes = new HashSet<NodeGlobalId>();//TODO хэш тут надо проверить
        var discovered = new HashSet<NodeGlobalId>(query.Nodes);//TODO хэш тут надо проверить
        var queue = new Queue<(NodeGlobalId NodeId, int Depth)>();

        foreach (var root in query.Nodes)
            queue.Enqueue((root, 0));

        var nodes = new Dictionary<NodeGlobalId, Node>();//TODO хэш тут надо проверить

        while (queue.Count > 0) {
            //cancellationTokens.Token.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visitedRequests.Add(path))
                continue;

            var result = await Get(path);
            if (result.Status == ServiceResultStatus.NotFound)
                continue;
            if (result.Status != ServiceResultStatus.Ok || result.Value is null)
                return ServiceResult<Subgraph>.From(result);

            var node = result.Value;
            if (!visitedNodes.Add(node.GlobalId))
                continue;

            nodes[node.GlobalId] = node;
            if (depth >= query.MaxDepth)
                continue;

            var connections = await GetConnectedNodesAsync(node);
            if (connections.Status != ServiceResultStatus.Ok || connections.Value is null)
                return ServiceResult<Subgraph>.From(connections);

            foreach (var neighborId in connections.Value.Select(x => x.GlobalId))
                if (neighborId != node.GlobalId && discovered.Add(neighborId))
                    queue.Enqueue((neighborId, depth + 1));
        }

        return ServiceResult<Subgraph>.Ok(nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            });
    }
}
