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
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string[] globalId) {
        var result = await _storage.Get(new NodeGlobalId(globalId));
        return ToActionResult<NodeState, NodeResponse>(result, static state => GraphResponseMapper.ToNodeResponse(new Node(state)));
    }

    [HttpGet("nodes/{globalId}/neighbor/{localId}")]
    public async Task<ActionResult<NodeResponse>> GetNeighborNodeAsync([FromRoute] string globalId, [FromRoute] string localId) {
        var result = await _storage.GetNeighbor(_storage.DeserializeGlobalId(globalId), new NodeLocalId(localId));
        return ToActionResult<NodeState, NodeResponse>(result, static state => GraphResponseMapper.ToNodeResponse(new Node(state)));
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request) {
        var result = await _storage.Create(new(request.LocalId), (NodeGlobalId?)request.ParentGlobalId, request.Attributes);
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            var globalId = result.Value.GlobalId;
            var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { globalId }) ?? $"/api/graph/nodes?{string.Join('&', globalId.Select(static segment => $"globalId={Uri.EscapeDataString(segment)}"))}";
            return Created(location, GraphResponseMapper.ToNodeResponse(new Node(result.Value)));
        }
        return ToActionResult<NodeState, NodeResponse>(result, static state => GraphResponseMapper.ToNodeResponse(new Node(state)));
    }

    [HttpPut("nodes")]
    public async Task<ActionResult<OperationResponse>> UpdateNodeAsync([FromQuery] string[] globalId, [FromBody] UpdateNodeRequest request) {
        var result = await _storage.Update(new NodeGlobalId(globalId), request.Attributes);
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result.Status, result.Error);
    }

    [HttpDelete("nodes")]
    public async Task<ActionResult<OperationResponse>> DeleteNodeAsync([FromQuery] string[] globalId) {
        var result = await _storage.Delete(new NodeGlobalId(globalId));
        return ToActionResult<OperationResponse>(result);
    }

    [HttpPost("connections")]
    public async Task<ActionResult<OperationResponse>> ConnectNodesAsync([FromBody] ConnectNodesRequest request) {
        var result = await _storage.Connect(new NodeGlobalId(request.SourceGlobalId), new NodeGlobalId(request.TargetGlobalId));
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request) {
        var result = await _storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = request.GlobalIds.Select(static x => new NodeGlobalId(x)).ToArray(),
            MaxDepth = request.MaxDepth,
        });
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpPost("search/nodes")]
    public async Task<ActionResult<IAsyncEnumerable<NodeSearchMatchResponse>>> SearchNodesAsync(
        [FromBody] NodeSearchQueryRequest request,
        CancellationToken cancellationToken) {
        if (request is null)
            return BadRequest();
        try {
            var query = GraphRequestMapper.ToNodeSearchQuery(request);
            await using var matches = _searchService
                .SearchNodesStreamAsync(query, cancellationToken)
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
            ServiceResultStatus.Conflict => Conflict(result.Error),
            ServiceResultStatus.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private ActionResult<T> ToActionResult<T>(ServiceResult result) {
        return ToActionResult<T>(result.Status, result.Error);
    }

    private ActionResult<T> ToActionResult<T>(ServiceResultStatus status, string? error) {
        return status switch {
            ServiceResultStatus.Ok => Ok(),
            ServiceResultStatus.BadRequest => BadRequest(error),
            ServiceResultStatus.NotFound => NotFound(),
            ServiceResultStatus.Conflict => Conflict(error),
            ServiceResultStatus.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
