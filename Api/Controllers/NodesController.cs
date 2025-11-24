using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class NodesController(NodeService nodeService) : ControllerBase
{
    private readonly NodeService _nodeService = nodeService;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NodeResponse>> GetAsync(string name)
    {
        var node = await _nodeService.Get(name);
        if (node is null)
        {
            return NotFound();
        }

        return Ok(ToResponse(node));
    }

    [HttpPost]
    public async Task<ActionResult<NodeResponse>> CreateAsync([FromBody] CreateNodeRequest request)
    {
        var created = await _nodeService.Create(null, request.Name);
        return CreatedAtAction(nameof(GetAsync), new { id = created.Name }, ToResponse(created));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(string name, [FromBody] UpdateNodeRequest request)
    {
        var node = await _nodeService.Get(name);
        if(node == null)
            return NotFound();
        await _nodeService.Update(node);
        return NoContent();
    }

    [HttpPost("{id:guid}/connect/{targetId:guid}")]
    public async Task<IActionResult> ConnectAsync(string name, string targetName)
    {
        var node = await _nodeService.Get(name);
        var targetNode =  await _nodeService.Get(targetName);
        if(node is null || targetNode is null)
            return NotFound();
        await _nodeService.ConnectNodes(node, targetNode);
        return NoContent();
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
