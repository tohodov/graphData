using System;
using System.Collections.Generic;
using System.Linq;
using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SubgraphsController(NodeService nodeService) : ControllerBase
{
    private readonly NodeService service = nodeService;

    [HttpPost]
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

        var roots = request.RootNodeIds?.Distinct().ToArray() ?? Array.Empty<NodeId>();
        if (roots.Length == 0)
        {
            return Ok(new SubgraphResponse());
        }

        var query = new SubgraphQuery
        {
            RootNodeIds = roots,
            MaxDepth = request.MaxDepth,
            IncludeDisconnectedRoots = request.IncludeDisconnectedRoots
        };

        var subgraph = await service.GetSubgraph(query);
        var nodes = subgraph.Nodes
            .Select(static node => new NodeResponse
            {
                Name = node.Name,
                Attributes = new Dictionary<string, string>(node.Attributes)
            })
            .ToArray();

        return Ok(new SubgraphResponse { Nodes = nodes });
    }
}
