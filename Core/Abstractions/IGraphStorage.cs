using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<ServiceResult<Node>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null);
    Task<ServiceResult<Node>> Get(NodeGlobalId path);

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

        var comparer = StringComparer.OrdinalIgnoreCase;
        var visited = new HashSet<NodeGlobalId>();//TODO хэш тут надо проверить
        var discovered = new HashSet<NodeGlobalId>(query.Nodes);//TODO хэш тут надо проверить
        var queue = new Queue<(NodeGlobalId NodeId, int Depth)>();

        foreach (var root in query.Nodes)
            queue.Enqueue((root, 0));

        var nodes = new Dictionary<NodeGlobalId, Node>();//TODO хэш тут надо проверить

        while (queue.Count > 0) {
            //cancellationTokens.Token.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path))
                continue;

            var result = await Get(path);
            if (result.Status == ServiceResultStatus.NotFound)
                continue;
            if (result.Status != ServiceResultStatus.Ok || result.Value is null)
                return ServiceResult<Subgraph>.From(result);

            var node = result.Value;
            nodes[path] = node;
            if (depth >= query.MaxDepth)
                continue;

            foreach (var neighborId in node.Nodes.Select(x => x.GlobalId))
                if (!neighborId.SequenceEqual(path) && discovered.Add(neighborId))
                    queue.Enqueue((neighborId, depth + 1));
        }

        return ServiceResult<Subgraph>.Ok(nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            });
    }
}
