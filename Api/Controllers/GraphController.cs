using System.Text.Json;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph")]
public sealed class GraphController(IGraphStorage storage, GraphSearchService searchService) : ControllerBase {
    private static readonly JsonSerializerOptions StreamJsonOptions = GraphJsonSerializerOptions.Create();

    private readonly IGraphStorage _storage = storage;
    private readonly GraphSearchService _searchService = searchService;

    [HttpGet("nodes")]
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string[] path) {
        var result = await _storage.Get(new NodeGlobalId(path));
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request) {
        var result = await _storage.Create(new(request.Name), (NodeGlobalId?)request.ParentPath, request.Attributes);
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            var path = result.Value.GlobalId;
            var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { path }) ?? $"/api/graph/nodes?{string.Join('&', path.Select(static segment => $"path={Uri.EscapeDataString(segment)}"))}";
            return Created(location, GraphResponseMapper.ToNodeResponse(result.Value));
        }
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPut("nodes")]
    public async Task<IActionResult> UpdateNodeAsync([FromQuery] string[] path, [FromBody] UpdateNodeRequest request) {
        var result = await _storage.Update(new NodeGlobalId(path), request.Attributes);
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult(result.Status, result.Error);
    }

    [HttpDelete("nodes")]
    public async Task<IActionResult> DeleteNodeAsync([FromQuery] string[] path) {
        var result = await _storage.Delete(new NodeGlobalId(path));
        return ToActionResult(result);
    }

    [HttpPost("connections")]
    public async Task<IActionResult> ConnectNodesAsync([FromBody] ConnectNodesRequest request) {
        var result = await _storage.Connect(request.SourcePath, request.TargetPath);
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult(result);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request) {
        var result = await _storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = request.Nodes.Select(static x => new NodeGlobalId(x)).ToArray(),
            MaxDepth = request.MaxDepth,
        });
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpPost("search/nodes")]
    public async Task<IActionResult> SearchNodesAsync(
        [FromBody] NodeSearchQuery request,
        CancellationToken cancellationToken) {
        if (request is null)
            return BadRequest();
        try {
            await using var matches = _searchService
                .SearchNodesStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var hasMatch = await matches.MoveNextAsync();

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson; charset=utf-8";

            while (hasMatch) {
                await JsonSerializer.SerializeAsync(
                    Response.Body,
                    GraphResponseMapper.ToSearchMatchResponse(matches.Current),
                    StreamJsonOptions,
                    cancellationToken);
                await Response.WriteAsync("\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);

                hasMatch = await matches.MoveNextAsync();
            }

            return new EmptyResult();
        } catch (ArgumentException ex) {
            if (Response.HasStarted)
                throw;
            return BadRequest(ex.Message);
        } catch (NotSupportedException ex) {
            if (Response.HasStarted)
                throw;
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
    }

    private ActionResult<TO> ToActionResult<FROM, TO>(ServiceResult<FROM> result, Func<FROM, TO> map) {
        return result.Status switch {
            ServiceResultStatus.Ok when result.Value is not null => Ok(map(result.Value)),
            ServiceResultStatus.BadRequest => BadRequest(result.Error),
            ServiceResultStatus.NotFound => NotFound(),
            ServiceResultStatus.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private IActionResult ToActionResult(ServiceResult result) {
        return ToActionResult(result.Status, result.Error);
    }

    private IActionResult ToActionResult(ServiceResultStatus status, string? error) {
        return status switch {
            ServiceResultStatus.Ok => Ok(),
            ServiceResultStatus.BadRequest => BadRequest(error),
            ServiceResultStatus.NotFound => NotFound(),
            ServiceResultStatus.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
