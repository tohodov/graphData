using System.Runtime.CompilerServices;
using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Api.Services;

public sealed class GraphApiService(NodeService nodeService, GraphSearchService searchService) {
    private readonly NodeService _nodeService = nodeService;
    private readonly GraphSearchService _searchService = searchService;

    public async Task<GraphApiResponse<NodeResponse>> GetNodeAsync(NodePath path) {
        var response = await GetNodeResponseAsync(path);
        return response is null
            ? GraphApiResponse<NodeResponse>.NotFound()
            : GraphApiResponse<NodeResponse>.Ok(response);
    }

    public async Task<GraphApiResponse<NodeResponse>> CreateNodeAsync(CreateNodeRequest request) {
        if (!NodeNameValidator.TryValidateSegment(request.Name, "Node name", out var validationError))
            return GraphApiResponse<NodeResponse>.BadRequest(validationError);

        Node? parent = null;
        if (request.ParentPath is { Length: > 0 }) {
            parent = await _nodeService.Get((NodePath)request.ParentPath);
            if (parent is null)
                return GraphApiResponse<NodeResponse>.NotFound($"Parent node '{request.ParentPath}' was not found.");
        }
        var created = await _nodeService.Create(parent, request.Name, request.Attributes);
        return GraphApiResponse<NodeResponse>.Ok(GraphResponseMapper.ToNodeResponse(created));
    }

    public async Task<GraphApiResponse<NodeResponse>> UpdateNodeAsync(NodePath path, UpdateNodeRequest request) {
        var node = await _nodeService.Get(path);
        if (node is null)
            return GraphApiResponse<NodeResponse>.NotFound();
        await _nodeService.Update(path, request.Attributes);
        var response = await GetNodeResponseAsync(path);
        return response is null
            ? GraphApiResponse<NodeResponse>.NotFound()
            : GraphApiResponse<NodeResponse>.Ok(response);
    }

    public async Task<GraphApiResponse> DeleteNodeAsync(NodePath path) {
        var node = await _nodeService.Get(path);//TODO оптимизировать двойное чтение
        if (node is null)
            return GraphApiResponse.NotFound();
        await _nodeService.Delete(path);//TODO оптимизировать двойное чтение
        return GraphApiResponse.Ok();
    }

    public async Task<GraphApiResponse> ConnectNodesAsync(ConnectNodesRequest request) {
        if (request.SourcePath.SequenceEqual(request.TargetPath))
            return GraphApiResponse.BadRequest("SourcePath and TargetPath must be different.");
        var source = await _nodeService.Get(request.SourcePath);
        var target = await _nodeService.Get(request.TargetPath);
        if (source is null || target is null)
            return GraphApiResponse.NotFound();
        try {
            await _nodeService.ConnectNodes(source, target);
        } catch (Exception ex) {
            return GraphApiResponse.InternalServerError(ex.ToString());
        }
        return GraphApiResponse.Ok();
    }

    public async Task<GraphApiResponse<SubgraphResponse>> GetSubgraphAsync(SubgraphRequest request) {
        if (request.MaxDepth < 0)
            return GraphApiResponse<SubgraphResponse>.BadRequest("MaxDepth must be non-negative.");
        if (request.Nodes.Count == 0)
            return GraphApiResponse<SubgraphResponse>.Ok(new SubgraphResponse());
        var subgraph = await _nodeService.GetSubgraph(new SubgraphQuery {
            Nodes = request.Nodes.Select(x => (NodePath)x).ToArray(),
            MaxDepth = request.MaxDepth,
        });

        return GraphApiResponse<SubgraphResponse>.Ok(GraphResponseMapper.ToSubgraphResponse(subgraph));
    }

    public async Task<GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>> SearchNodesAsync(
        NodeSearchQuery? request) {
        if (request is null) {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.BadRequest();
        }

        try {
            var matches = await _searchService.SearchNodesAsync(request);
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.Ok(
                matches.Select(GraphResponseMapper.ToSearchMatchResponse).ToArray());
        } catch (ArgumentException ex) {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.BadRequest(ex.Message);
        } catch (NotSupportedException ex) {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.NotImplemented(ex.Message);
        }
    }

    public async IAsyncEnumerable<NodeSearchMatchResponse> SearchNodesStreamAsync(
        NodeSearchQuery request,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var match in _searchService
                           .SearchNodesStreamAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken)) {
            yield return GraphResponseMapper.ToSearchMatchResponse(match);
        }
    }

    private async Task<NodeResponse?> GetNodeResponseAsync(NodePath path) {
        var neighborhood = await _nodeService.GetNeighborhood(path);
        if (neighborhood is null)
            return null;
        var (node, connections) = neighborhood.Value;
        return GraphResponseMapper.ToNodeResponse(
            node,
            connections.Select(connected => GraphResponseMapper.ToEdgeResponse(node, connected)));
    }
}
