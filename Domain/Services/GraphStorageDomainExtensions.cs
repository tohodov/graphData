using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal static class GraphStorageDomainExtensions {
    public static async Task<ServiceResult<Subgraph>> GetSubgraphAsync(this IGraphStorage storage, SubgraphQuery query) { //TODO переосмыслить
        var roots = new List<NodeState>();
        if (!query.Nodes.Any())
            roots.AddRange((await storage.Get(storage.Root)).Value!.Nodes);
        else
            foreach (var rootRef in query.Nodes) {
                var rootsResult = await storage.Get(rootRef);
                if (rootsResult.Status != ServiceResultStatus.Ok || rootsResult.Value is null)
                    return ServiceResult<Subgraph>.From(rootsResult);
                var node = rootsResult.Value;
                if (node.GlobalId != storage.Root)
                    roots.Add(node);
                else
                    roots.AddRange(node.Nodes);
            }

        var visitedRequests = new HashSet<InternalId>();//TODO хэш тут надо проверить
        var visitedNodes = new HashSet<InternalId>();//TODO хэш тут надо проверить
        var discovered = new HashSet<InternalId>(roots.Select(x => x.GlobalId));//TODO хэш тут надо проверить
        var queue = new Queue<(InternalId NodeId, int Depth)>();

        foreach (var root in roots)
            queue.Enqueue((root.GlobalId, 0));

        var nodes = new Dictionary<InternalId, Node>();//TODO хэш тут надо проверить

        while (queue.Count > 0) {
            //cancellationTokens.Token.ThrowIfCancellationRequested(); //TODO перенести внутрь GraphService
            var (path, depth) = queue.Dequeue();
            if (!visitedRequests.Add(path))
                continue;

            var result = await storage.Get(path);
            if (result.Status == ServiceResultStatus.NotFound)
                continue;
            if (result.Status != ServiceResultStatus.Ok || result.Value is null)
                return ServiceResult<Subgraph>.From(result);

            var node = new Node(result.Value);
            if (!visitedNodes.Add(node.GlobalId))
                continue;

            nodes[node.GlobalId] = node;
            if (depth >= query.MaxDepth)
                continue;

            var connections = await storage.GetConnectedNodesAsync(result.Value);
            if (connections.Status != ServiceResultStatus.Ok || connections.Value is null)
                return ServiceResult<Subgraph>.From(connections);

            foreach (var neighborId in connections.Value.Select(static x => x.GlobalId))
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
