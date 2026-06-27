global using InternalId = Abstractions.NodeRef.InternalId;
global using NodePath = Abstractions.NodeRef.NodePath;
using System.Text.Json;
using Abstractions;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph")]
public sealed class GraphController(GraphService graph) : ControllerBase {
    static readonly JsonSerializerOptions StreamJsonOptions = GraphJsonSerializerOptions.Create();

    readonly GraphService graph = graph;

    [HttpGet("nodes")]
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string[] globalId) {
        var result = await graph.GetNodeAsync(new NodePath(globalId));
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpGet("nodes/{globalId}/neighbor/{localId}")]
    public async Task<ActionResult<NodeResponse>> GetNeighborNodeAsync([FromRoute] string globalId, [FromRoute] string localId) {
        var decoded = Uri.UnescapeDataString(globalId);
        var segments = decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment)
            .Select(x => new NodeLocalId(x));
        var internalId = new InternalId(segments);
        var result = await graph.GetNeighborNodeAsync(internalId, localId);
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request) {
        var result = await graph.CreateNode((NodeLocalId)request.LocalId, (NodePath?)request.ParentPath, attributes: request.Attributes);
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            var globalId = result.Value.GlobalId;
            var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { globalId }) ?? $"/api/graph/nodes?{string.Join('&', globalId.Select(static segment => $"globalId={Uri.EscapeDataString(segment)}"))}";
            return Created(location, GraphResponseMapper.ToNodeResponse(result.Value));
        }
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPut("nodes")]
    public async Task<ActionResult<OperationResponse>> UpdateNodeAsync([FromQuery] string[] path, [FromBody] UpdateNodeRequest request) {
        var result = await graph.UpdateNodeAsync(new NodePath(path), request.Attributes);
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result.Status, result.Error);
    }

    [HttpDelete("nodes")]
    public async Task<ActionResult<OperationResponse>> DeleteNodeAsync([FromQuery] string[] path) {
        var result = await graph.DeleteNodeAsync(new NodePath(path));
        return ToActionResult<OperationResponse>(result);
    }

    [HttpPost("connections")]
    public async Task<ActionResult<OperationResponse>> ConnectNodesAsync([FromBody] ConnectNodesRequest request) {
        var result = await graph.ConnectNodesAsync(new InternalId(request.Node1InternalId.Select(x => new NodeLocalId(x))), new InternalId(request.Node2InternalId.Select(x => new NodeLocalId(x))));
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result);
    }

    [HttpPut("nodes/type")]
    public async Task<ActionResult<SubgraphResponse>> AssignNodeTypeAsync([FromBody] AssignNodeTypeRequest request) {
        var result = await graph.AssignNodeTypeAsync(new InternalId(request.InternalId.Select(x => new NodeLocalId(x))), new InternalId(request.TypeGlobalId.Select(x => new NodeLocalId(x))));
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpPut("edges/type")]
    public async Task<ActionResult<SubgraphResponse>> ChangeEdgeTypeAsync([FromBody] ChangeEdgeTypeRequest request) {
        var result = await graph.ChangeEdgeTypeAsync(
            new InternalId(request.Node1InternalId.Select(x => new NodeLocalId(x))),
            new InternalId(request.Node2InternalId.Select(x => new NodeLocalId(x))),
            new InternalId(request.TypeGlobalId.Select(x => new NodeLocalId(x))));
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<SubgraphResponse>> GetSubgraphAsync([FromBody] SubgraphRequest request) {
        var result = await graph.GetSubgraph(request.Paths.Select(x => new NodePath(x)), request.MaxDepth);
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
            await using var matches = graph
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
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
