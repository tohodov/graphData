using System.Runtime.CompilerServices;
using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Api.Services;

public sealed class GraphApiService(
    NodeService nodeService,
    GraphSearchService searchService)
{
    private readonly NodeService _nodeService = nodeService;
    private readonly GraphSearchService _searchService = searchService;

    public async Task<GraphApiResponse<NodeResponse>> GetNodeAsync(string name)
    {
        if (!NodeNameValidator.TryValidate(name, out var validationError))
        {
            return GraphApiResponse<NodeResponse>.BadRequest(validationError);
        }

        var response = await GetNodeResponseAsync(name);
        return response is null
            ? GraphApiResponse<NodeResponse>.NotFound()
            : GraphApiResponse<NodeResponse>.Ok(response);
    }

    public async Task<GraphApiResponse<NodeResponse>> CreateNodeAsync(CreateNodeRequest? request)
    {
        if (request is null)
        {
            return GraphApiResponse<NodeResponse>.BadRequest();
        }

        if (!NodeNameValidator.TryValidate(request.Name, out var validationError))
        {
            return GraphApiResponse<NodeResponse>.BadRequest(validationError);
        }

        Node? parent = null;
        if (!string.IsNullOrWhiteSpace(request.ParentName))
        {
            if (!NodeNameValidator.TryValidate(request.ParentName, "Parent node name", out validationError))
            {
                return GraphApiResponse<NodeResponse>.BadRequest(validationError);
            }

            parent = await _nodeService.Get(request.ParentName);
            if (parent is null)
            {
                return GraphApiResponse<NodeResponse>.NotFound(
                    $"Parent node '{request.ParentName}' was not found.");
            }
        }

        var created = await _nodeService.Create(parent, request.Name, request.Attributes);
        return GraphApiResponse<NodeResponse>.Created(GraphResponseMapper.ToNodeResponse(created));
    }

    public async Task<GraphApiResponse<NodeResponse>> UpdateNodeAsync(
        string name,
        UpdateNodeRequest? request)
    {
        if (!NodeNameValidator.TryValidate(name, out var validationError))
        {
            return GraphApiResponse<NodeResponse>.BadRequest(validationError);
        }

        if (request is null)
        {
            return GraphApiResponse<NodeResponse>.BadRequest();
        }

        var node = await _nodeService.Get(name);
        if (node is null)
        {
            return GraphApiResponse<NodeResponse>.NotFound();
        }

        await _nodeService.Update(name, request.Attributes ?? new Dictionary<string, string>());
        var response = await GetNodeResponseAsync(name);
        return response is null
            ? GraphApiResponse<NodeResponse>.NotFound()
            : GraphApiResponse<NodeResponse>.Ok(response);
    }

    public async Task<GraphApiResponse> DeleteNodeAsync(string name)
    {
        if (!NodeNameValidator.TryValidate(name, out var validationError))
        {
            return GraphApiResponse.BadRequest(validationError);
        }

        var node = await _nodeService.Get(name);
        if (node is null)
        {
            return GraphApiResponse.NotFound();
        }

        await _nodeService.Delete(name);
        return GraphApiResponse.NoContent();
    }

    public async Task<GraphApiResponse> ConnectNodesAsync(ConnectNodesRequest? request)
    {
        if (request is null)
        {
            return GraphApiResponse.BadRequest();
        }

        if (!NodeNameValidator.TryValidate(request.SourceName, "Source node name", out var validationError))
        {
            return GraphApiResponse.BadRequest(validationError);
        }

        if (!NodeNameValidator.TryValidate(request.TargetName, "Target node name", out validationError))
        {
            return GraphApiResponse.BadRequest(validationError);
        }

        if (string.Equals(request.SourceName, request.TargetName, StringComparison.OrdinalIgnoreCase))
        {
            return GraphApiResponse.BadRequest("SourceName and TargetName must be different.");
        }

        var source = await _nodeService.Get(request.SourceName);
        var target = await _nodeService.Get(request.TargetName);
        if (source is null || target is null)
        {
            return GraphApiResponse.NotFound();
        }

        await _nodeService.ConnectNodes(source, target);
        return GraphApiResponse.NoContent();
    }

    public async Task<GraphApiResponse<SubgraphResponse>> GetSubgraphAsync(SubgraphRequest? request)
    {
        if (request is null)
        {
            return GraphApiResponse<SubgraphResponse>.BadRequest();
        }

        if (request.MaxDepth < 0)
        {
            return GraphApiResponse<SubgraphResponse>.BadRequest("MaxDepth must be non-negative.");
        }

        var roots = request.RootNodeIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (roots.Length == 0)
        {
            return GraphApiResponse<SubgraphResponse>.Ok(new SubgraphResponse());
        }

        foreach (var root in roots)
        {
            if (!NodeNameValidator.TryValidate(root, "Root node name", out var validationError))
            {
                return GraphApiResponse<SubgraphResponse>.BadRequest(validationError);
            }
        }

        var subgraph = await _nodeService.GetSubgraph(new SubgraphQuery
        {
            RootNodeIds = roots,
            MaxDepth = request.MaxDepth,
            IncludeDisconnectedRoots = request.IncludeDisconnectedRoots
        });

        return GraphApiResponse<SubgraphResponse>.Ok(GraphResponseMapper.ToSubgraphResponse(subgraph));
    }

    public async Task<GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>> SearchNodesAsync(
        NodeSearchQuery? request)
    {
        if (request is null)
        {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.BadRequest();
        }

        try
        {
            var matches = await _searchService.SearchNodesAsync(request);
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.Ok(
                matches.Select(GraphResponseMapper.ToSearchMatchResponse).ToArray());
        }
        catch (ArgumentException ex)
        {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.BadRequest(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return GraphApiResponse<IReadOnlyCollection<NodeSearchMatchResponse>>.NotImplemented(ex.Message);
        }
    }

    public async IAsyncEnumerable<NodeSearchMatchResponse> SearchNodesStreamAsync(
        NodeSearchQuery request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var match in _searchService
                           .SearchNodesStreamAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return GraphResponseMapper.ToSearchMatchResponse(match);
        }
    }

    private async Task<NodeResponse?> GetNodeResponseAsync(string name)
    {
        var neighborhood = await _nodeService.GetNeighborhood(name);
        if (neighborhood is null)
        {
            return null;
        }

        var (node, connections) = neighborhood.Value;
        return GraphResponseMapper.ToNodeResponse(
            node,
            connections.Select(connected => GraphResponseMapper.ToEdgeResponse(node, connected)));
    }
}
