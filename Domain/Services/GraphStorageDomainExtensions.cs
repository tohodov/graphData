using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal static class GraphStorageDomainExtensions
{
    public static async Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(this IGraphStorage storage, Node node) {
        var result = await storage.GetConnectedNodesAsync(node.RequireState());
        return result.Status == ServiceResultStatus.Ok && result.Value is not null
            ? ServiceResult<IReadOnlyCollection<Node>>.Ok(result.Value.Select(static state => new Node(state)).ToArray())
            : ServiceResult<IReadOnlyCollection<Node>>.From(result);
    }

    public static async Task<ServiceResult<Subgraph>> GetSubgraphAsync(this IGraphStorage storage, SubgraphQuery query) { //TODO переосмыслить
        var rootsResult = await ResolveSubgraphRootsAsync(storage, query.Nodes);
        if (rootsResult.Status != ServiceResultStatus.Ok || rootsResult.Value is null)
            return ServiceResult<Subgraph>.From(rootsResult);

        var roots = rootsResult.Value;
        if (roots.Count == 0)
            return ServiceResult<Subgraph>.Ok(new Subgraph { Nodes = [] });

        var visitedRequests = new HashSet<NodeGlobalId>();//TODO хэш тут надо проверить
        var visitedNodes = new HashSet<NodeGlobalId>();//TODO хэш тут надо проверить
        var discovered = new HashSet<NodeGlobalId>(roots);//TODO хэш тут надо проверить
        var queue = new Queue<(NodeGlobalId NodeId, int Depth)>();

        foreach (var root in roots)
            queue.Enqueue((root, 0));

        var nodes = new Dictionary<NodeGlobalId, Node>();//TODO хэш тут надо проверить

        while (queue.Count > 0) {
            //cancellationTokens.Token.ThrowIfCancellationRequested();
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

    private static async Task<ServiceResult<IReadOnlyCollection<NodeGlobalId>>> ResolveSubgraphRootsAsync(
        IGraphStorage storage,
        IReadOnlyCollection<NodeGlobalId> requestedNodes) {
        if (requestedNodes.Count > 0 && requestedNodes.All(static node => node.Any()))
            return ServiceResult<IReadOnlyCollection<NodeGlobalId>>.Ok(requestedNodes);

        var explicitRoots = requestedNodes
            .Where(static node => node.Any())
            .ToList();

        if (storage is not IGraphNodeCatalog catalog)
            return ServiceResult<IReadOnlyCollection<NodeGlobalId>>.Ok(explicitRoots);

        var catalogRoots = (await catalog.GetRootNodesAsync())
            .Select(static node => node.GlobalId)
            .Where(static globalId => globalId.Any());

        explicitRoots.AddRange(catalogRoots);
        return ServiceResult<IReadOnlyCollection<NodeGlobalId>>.Ok(explicitRoots.Distinct().ToArray());
    }
}
