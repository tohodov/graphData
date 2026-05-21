using System.Runtime.CompilerServices;
using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph")]
public sealed class GraphController(
    NodeService nodeService,
    GraphSearchService searchService) : ControllerBase
{
    private readonly NodeService _nodeService = nodeService;
    private readonly GraphSearchService _searchService = searchService;

    [HttpGet("nodes")]
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Node name must be provided.");
        }

        var neighborhood = await _nodeService.GetNeighborhood(name);
        if (neighborhood is null)
        {
            return NotFound();
        }

        var (node, connections) = neighborhood.Value;
        return Ok(ToNodeResponse(node, connections.Select(connected => ToEdgeResponse(node, connected))));
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Node name must be provided.");
        }

        var created = await _nodeService.Create(null, request.Name, request.Attributes);
        var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { name = created.Name })
            ?? $"/api/graph/nodes?name={Uri.EscapeDataString(created.Name)}";
        return Created(location, ToNodeResponse(created));
    }

    [HttpPut("nodes")]
    public async Task<IActionResult> UpdateNodeAsync([FromQuery] string name, [FromBody] UpdateNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Node name must be provided.");
        }

        if (request is null)
        {
            return BadRequest();
        }

        var node = await _nodeService.Get(name);
        if (node is null)
        {
            return NotFound();
        }

        await _nodeService.Update(name, request.Attributes ?? new Dictionary<string, string>());
        return NoContent();
    }

    [HttpDelete("nodes")]
    public async Task<IActionResult> DeleteNodeAsync([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Node name must be provided.");
        }

        var node = await _nodeService.Get(name);
        if (node is null)
        {
            return NotFound();
        }

        await _nodeService.Delete(name);
        return NoContent();
    }

    [HttpPost("connections")]
    public async Task<IActionResult> ConnectNodesAsync([FromBody] ConnectNodesRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.SourceName) ||
            string.IsNullOrWhiteSpace(request.TargetName))
        {
            return BadRequest("SourceName and TargetName must be provided.");
        }

        if (string.Equals(request.SourceName, request.TargetName, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("SourceName and TargetName must be different.");
        }

        var source = await _nodeService.Get(request.SourceName);
        var target = await _nodeService.Get(request.TargetName);
        if (source is null || target is null)
        {
            return NotFound();
        }

        await _nodeService.ConnectNodes(source, target);
        return NoContent();
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request)
    {
        if (request is null)
        {
            return BadRequest();
        }

        if (request.MaxDepth < 0)
        {
            return BadRequest("MaxDepth must be non-negative.");
        }

        var roots = request.RootNodeIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (roots.Length == 0)
        {
            return Ok(new SubgraphResponse());
        }

        var subgraph = await _nodeService.GetSubgraph(new SubgraphQuery
        {
            RootNodeIds = roots,
            MaxDepth = request.MaxDepth,
            IncludeDisconnectedRoots = request.IncludeDisconnectedRoots
        });

        return Ok(ToSubgraphResponse(subgraph));
    }

    [HttpPost("search/nodes")]
    public async Task<ActionResult<NodeSearchResponse>> SearchNodesAsync([FromBody] NodeSearchQuery request)
    {
        if (request is null)
        {
            return BadRequest();
        }

        try
        {
            var matches = await _searchService.SearchNodesAsync(request);
            return Ok(new NodeSearchResponse
            {
                Matches = matches.Select(ToSearchMatchResponse).ToArray()
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
    }

    [HttpPost("search/nodes/stream")]
    public ActionResult<IAsyncEnumerable<NodeSearchMatchResponse>> SearchNodesStreamAsync(
        [FromBody] NodeSearchQuery request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest();
        }

        return Ok(StreamSearchNodesAsync(request, cancellationToken));
    }

    private async IAsyncEnumerable<NodeSearchMatchResponse> StreamSearchNodesAsync(
        NodeSearchQuery request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var match in _searchService.SearchNodesStreamAsync(request, cancellationToken))
        {
            yield return ToSearchMatchResponse(match);
        }
    }

    private static SubgraphResponse ToSubgraphResponse(Subgraph subgraph)
    {
        var nodeNames = subgraph.Nodes
            .Select(static node => node.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = subgraph.Nodes
            .SelectMany(node => node.Nodes.Select(connected => ToEdgeResponse(node, connected)))
            .Where(edge => nodeNames.Contains(edge.SourceName) && nodeNames.Contains(edge.TargetName))
            .DistinctBy(static edge => EdgeKey(edge.SourceName, edge.TargetName))
            .OrderBy(static edge => edge.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SubgraphResponse
        {
            Nodes = subgraph.Nodes
                .OrderBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static node => ToNodeResponse(node))
                .ToArray(),
            Edges = edges
        };
    }

    private static NodeResponse ToNodeResponse(
        Node node,
        IEnumerable<EdgeResponse>? edges = null)
    {
        return new NodeResponse
        {
            Name = node.Name,
            Attributes = new Dictionary<string, string>(node.Attributes),
            Edges = edges?.ToArray() ?? Array.Empty<EdgeResponse>()
        };
    }

    private static NodeSearchMatchResponse ToSearchMatchResponse(NodeSearchMatch match)
    {
        return new NodeSearchMatchResponse
        {
            Node = ToNodeResponse(match.Node),
            Bindings = match.Bindings.ToDictionary(
                static binding => binding.Key,
                static binding => ToNodeResponse(binding.Value),
                StringComparer.OrdinalIgnoreCase),
            Score = match.Score,
            MatchedBy = match.MatchedBy
        };
    }

    private static EdgeResponse ToEdgeResponse(Node source, Node target)
    {
        return ToEdgeResponse(source.Name, target.Name);
    }

    private static EdgeResponse ToEdgeResponse(string sourceName, string targetName)
    {
        return string.Compare(sourceName, targetName, StringComparison.OrdinalIgnoreCase) <= 0
            ? new EdgeResponse { SourceName = sourceName, TargetName = targetName }
            : new EdgeResponse { SourceName = targetName, TargetName = sourceName };
    }

    private static string EdgeKey(string sourceName, string targetName)
    {
        return string.Compare(sourceName, targetName, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{sourceName}\0{targetName}"
            : $"{targetName}\0{sourceName}";
    }
}
