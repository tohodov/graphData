using GraphData.Api.Models;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class NodesController(INodeService nodeService) : ControllerBase
{
    private readonly INodeService _nodeService = nodeService;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NodeResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var node = await _nodeService.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return NotFound();
        }

        return Ok(ToResponse(node));
    }

    [HttpPost]
    public async Task<ActionResult<NodeResponse>> CreateAsync([FromBody] CreateNodeRequest request, CancellationToken cancellationToken)
    {
        var metadata = new NodeMetadata
        {
            Id = Guid.Empty,
            Name = request.Name,
            Attributes = request.Attributes is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.Attributes)
        };

        var created = await _nodeService.CreateNodeAsync(metadata, cancellationToken).ConfigureAwait(false);
        var details = await _nodeService.GetNodeAsync(created.Id, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, ToResponse(details!));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] UpdateNodeRequest request, CancellationToken cancellationToken)
    {
        var metadata = new NodeMetadata
        {
            Id = id,
            Name = request.Name,
            Attributes = request.Attributes is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.Attributes)
        };

        await _nodeService.UpdateNodeAsync(metadata, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{id:guid}/connect/{targetId:guid}")]
    public async Task<IActionResult> ConnectAsync(Guid id, Guid targetId, CancellationToken cancellationToken)
    {
        await _nodeService.ConnectNodesAsync(id, targetId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("{id:guid}/connections")]
    public async Task<ActionResult<IReadOnlyCollection<Guid>>> GetConnectionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var node = await _nodeService.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return NotFound();
        }

        return Ok(node.Connections);
    }

    private static NodeResponse ToResponse(NodeDetails node)
    {
        return new NodeResponse
        {
            Id = node.Metadata.Id,
            Name = node.Metadata.Name,
            Attributes = new Dictionary<string, string>(node.Metadata.Attributes),
            Connections = node.Connections
        };
    }
}
