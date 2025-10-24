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
public sealed class SubgraphsController(INodeService nodeService) : ControllerBase
{
    private readonly INodeService _nodeService = nodeService;

    [HttpPost]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest();
        }

        if (request.MaxDepth < 0)
        {
            return BadRequest("MaxDepth must be non-negative.");
        }

        var roots = request.RootNodeIds?.Where(static id => id != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
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

        var subgraph = await _nodeService.GetSubgraphAsync(query, cancellationToken).ConfigureAwait(false);
        var nodes = subgraph.Nodes.Values
            .Select(static node => new NodeResponse
            {
                Id = node.Metadata.Id,
                Name = node.Metadata.Name,
                Attributes = new Dictionary<string, string>(node.Metadata.Attributes),
                Connections = node.Connections
            })
            .ToArray();

        return Ok(new SubgraphResponse { Nodes = nodes });
    }
}
