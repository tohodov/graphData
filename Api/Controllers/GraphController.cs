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
[Route("api/graph/raw")]
[Route("api/graph")]
public sealed class RawGraphController(GraphService graph) : GraphControllerBase {
    static readonly JsonSerializerOptions StreamJsonOptions = GraphJsonSerializerOptions.Create();

    readonly GraphService graph = graph;

    [HttpGet("nodes")]
    public async Task<ActionResult<NodeResponse>> GetNodeAsync([FromQuery] string[] globalId) {
        var result = await graph.GetNode(new NodePath(globalId));
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpGet("nodes/{globalId}/neighbor/{localId}")]
    public async Task<ActionResult<NodeResponse>> GetNeighborNodeAsync([FromRoute] string globalId, [FromRoute] string localId) {
        var decoded = Uri.UnescapeDataString(globalId);//TODO убрать из API и WEB UI globalId
        var segments = decoded
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment)
            .Select(x => new NodeLocalId(x));
        var path = new NodePath(segments);
        var result = await graph.GetNode(path, localId);
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPost("nodes")]
    public async Task<ActionResult<NodeResponse>> CreateNodeAsync([FromBody] CreateNodeRequest request) {
        ServiceResult<Node> result;
        try {
            result = await graph.CreateNode((NodeLocalId)request.LocalId, (NodePath?)request.ParentPath, attributes: request.Attributes);
        } catch (Exception ex) {
            return BadRequest(ex.Message);
        }

        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            var globalId = result.Value.GlobalId;
            var location = Url?.ActionLink(nameof(GetNodeAsync), values: new { globalId }) ?? $"/api/graph/raw/nodes?{string.Join('&', globalId.Select(static segment => $"globalId={Uri.EscapeDataString(segment)}"))}";
            return Created(location, GraphResponseMapper.ToNodeResponse(result.Value));
        }
        return ToActionResult<Node, NodeResponse>(result, static node => GraphResponseMapper.ToNodeResponse(node));
    }

    [HttpPut("nodes")]
    public async Task<ActionResult<OperationResponse>> UpdateNodeAsync(
        [FromQuery] string[] globalId,
        [FromQuery] string[] path,
        [FromBody] UpdateNodeRequest request) {
        if (TryCreateRequiredNodePath(globalId, path, out var nodePath, out var error) == false)
            return BadRequest(error);

        var result = await graph.UpdateNode(nodePath, request.Attributes);
        return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result.Status, result.Error);
    }

    [HttpDelete("nodes")]
    public async Task<ActionResult<OperationResponse>> DeleteNodeAsync(
        [FromQuery] string[] globalId,
        [FromQuery] string[] path) {
        if (TryCreateRequiredNodePath(globalId, path, out var nodePath, out var error) == false)
            return BadRequest(error);

        var result = await graph.DeleteNode(nodePath);
        return ToActionResult<OperationResponse>(result);
    }

    [HttpPost("connections")]
    public async Task<ActionResult<OperationResponse>> ConnectNodesAsync([FromBody] ConnectNodesRequest request) {
        try {
            var result = await graph.ConnectNodesAsync(new NodePath(request.Node1InternalId.Select(x => new NodeLocalId(x))), new NodePath(request.Node2InternalId.Select(x => new NodeLocalId(x))));
            return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result);
        } catch (InvalidOperationException ex) {
            return StatusCode(StatusCodes.Status500InternalServerError, $"{ex.GetType().Name}: {ex.Message}");
        } catch (Exception ex) {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("connections")]
    public async Task<ActionResult<OperationResponse>> DisconnectNodesAsync([FromBody] ConnectNodesRequest request) {
        try {
            var result = await graph.Disconnect(new NodePath(request.Node1InternalId.Select(x => new NodeLocalId(x))), new NodePath(request.Node2InternalId.Select(x => new NodeLocalId(x))));
            return result.Status == ServiceResultStatus.Ok ? NoContent() : ToActionResult<OperationResponse>(result);
        } catch (InvalidOperationException ex) {
            return StatusCode(StatusCodes.Status500InternalServerError, $"{ex.GetType().Name}: {ex.Message}");
        } catch (Exception ex) {
            return BadRequest(ex.Message);
        }
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

}
