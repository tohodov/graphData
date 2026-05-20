using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph-viewer")]
public sealed class GraphViewerController(NodeService nodeService) : ControllerBase
{
    private readonly NodeService _nodeService = nodeService;

    [HttpGet("nodes")]
    public async Task<ActionResult<GraphViewerNodeResponse>> GetNodeAsync([FromQuery] string name)
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
        return Ok(new GraphViewerNodeResponse
        {
            Node = ToResponse(node),
            Edges = connections
                .OrderBy(static connected => connected.Name, StringComparer.OrdinalIgnoreCase)
                .Select(connected => new GraphViewerEdgeResponse
                {
                    SourceName = node.Name,
                    TargetName = connected.Name,
                    TargetNode = ToResponse(connected)
                })
                .ToArray()
        });
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
