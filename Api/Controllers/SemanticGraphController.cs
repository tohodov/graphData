using Abstractions;
using GraphData.Api.Models;
using GraphData.Api.Services;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph/semantic")]
public sealed class SemanticGraphController(GraphService graph) : ControllerBase
{
    [HttpPost("traverse")]
    public async Task<ActionResult<GraphObservationResponse>> TraverseAsync([FromBody] GraphTraversalRequest request)
    {
        var roots = (request.Roots ?? Array.Empty<IReadOnlyCollection<string>>())
            .Select(static root => new NodePath(root.Select(static segment => new NodeLocalId(segment))));
        var result = await graph.TraverseAsync(
            roots,
            request.Traversal,
            request.MaxNodes,
            request.MaxEdges).ConfigureAwait(false);
        return ToActionResult<GraphObservation, GraphObservationResponse>(
            result,
            GraphResponseMapper.ToGraphObservationResponse);
    }

    private ActionResult<TO> ToActionResult<FROM, TO>(ServiceResult<FROM> result, Func<FROM, TO> map)
    {
        if (result.Status == ServiceResultStatus.Ok && result.Value is not null)
            return Ok(map(result.Value));
        return ToActionResult<TO>(result.Status, result.Error);
    }

    private ActionResult<T> ToActionResult<T>(ServiceResultStatus status, string? error)
    {
        return status switch
        {
            ServiceResultStatus.NotFound => NotFound(error),
            ServiceResultStatus.Conflict => Conflict(error),
            ServiceResultStatus.BadRequest => BadRequest(error),
            _ => StatusCode(StatusCodes.Status500InternalServerError, error)
        };
    }
}
