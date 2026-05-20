using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(GraphSearchService searchService) : ControllerBase
{
    private readonly GraphSearchService _searchService = searchService;

    [HttpPost("nodes")]
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
                Matches = matches.Select(static match => new NodeSearchMatchResponse
                {
                    Node = ToResponse(match.Node),
                    Score = match.Score,
                    MatchedBy = match.MatchedBy
                }).ToArray()
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

    private static NodeResponse ToResponse(Node node)
    {
        return new NodeResponse
        {
            Name = node.Name,
            Attributes = new Dictionary<string, string>(node.Attributes)
        };
    }
}
