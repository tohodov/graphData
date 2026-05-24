using System.Text.Json;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph")]
public sealed class GraphController(GraphApiService graphApi) : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJsonOptions = GraphJsonSerializerOptions.Create();

    private readonly GraphApiService _graphApi = graphApi;

    [HttpGet("nodes")]
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string[] path)
    {
        var result = await _graphApi.GetNodeAsync(path);
        return ToActionResult(result);
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request)
    {
        var result = await _graphApi.CreateNodeAsync(request);
        if (result.Status is GraphApiStatus.Created && result.Value is not null)
        {
            var path = result.Value.Name.Split('/');
            var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { path })
                ?? $"/api/graph/nodes?{string.Join('&', path.Select(static segment => $"path={Uri.EscapeDataString(segment)}"))}";
            return Created(location, result.Value);
        }

        return ToActionResult(result);
    }

    [HttpPut("nodes")]
    public async Task<IActionResult> UpdateNodeAsync([FromQuery] string[] path, [FromBody] UpdateNodeRequest request)
    {
        var result = await _graphApi.UpdateNodeAsync(path, request);
        return result.Succeeded ? NoContent() : ToActionResult(result.Status, result.Error);
    }

    [HttpDelete("nodes")]
    public async Task<IActionResult> DeleteNodeAsync([FromQuery] string[] path)
    {
        var result = await _graphApi.DeleteNodeAsync(path);
        return ToActionResult(result);
    }

    [HttpPost("connections")]
    public async Task<IActionResult> ConnectNodesAsync([FromBody] ConnectNodesRequest request)
    {
        var result = await _graphApi.ConnectNodesAsync(request);
        return ToActionResult(result);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request)
    {
        var result = await _graphApi.GetSubgraphAsync(request);
        return ToActionResult(result);
    }

    [HttpPost("search/nodes")]
    public async Task<IActionResult> SearchNodesAsync(
        [FromBody] NodeSearchQuery request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest();
        }

        try
        {
            await using var matches = _graphApi
                .SearchNodesStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var hasMatch = await matches.MoveNextAsync();

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson; charset=utf-8";

            while (hasMatch)
            {
                await JsonSerializer.SerializeAsync(
                    Response.Body,
                    matches.Current,
                    StreamJsonOptions,
                    cancellationToken);
                await Response.WriteAsync("\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);

                hasMatch = await matches.MoveNextAsync();
            }

            return new EmptyResult();
        }
        catch (ArgumentException ex)
        {
            if (Response.HasStarted)
            {
                throw;
            }

            return BadRequest(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            if (Response.HasStarted)
            {
                throw;
            }

            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
    }

    private ActionResult<T> ToActionResult<T>(GraphApiResponse<T> result)
    {
        return result.Status switch
        {
            GraphApiStatus.Ok when result.Value is not null => Ok(result.Value),
            GraphApiStatus.Created when result.Value is not null => Created(string.Empty, result.Value),
            GraphApiStatus.BadRequest => BadRequest(result.Error),
            GraphApiStatus.NotFound => NotFound(),
            GraphApiStatus.NotImplemented => StatusCode(StatusCodes.Status501NotImplemented, result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private IActionResult ToActionResult(GraphApiResponse result)
    {
        return ToActionResult(result.Status, result.Error);
    }

    private IActionResult ToActionResult(GraphApiStatus status, string? error)
    {
        return status switch
        {
            GraphApiStatus.NoContent => NoContent(),
            GraphApiStatus.BadRequest => BadRequest(error),
            GraphApiStatus.NotFound => NotFound(),
            GraphApiStatus.NotImplemented => StatusCode(StatusCodes.Status501NotImplemented, error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
