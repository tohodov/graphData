using Abstractions;
using GraphData.Api.Models;
using GraphData.Api.Services;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/graph/semantic")]
[Route("api/graph")]
public sealed class SemanticGraphController(GraphService graph) : GraphControllerBase
{
    readonly GraphService graph = graph;

    [HttpPut("nodes/type")]
    public async Task<ActionResult<SubgraphResponse>> AssignNodeTypeAsync([FromBody] AssignNodeTypeRequest request)
    {
        var result = await graph.AssignNodeTypeAsync(new NodePath(request.InternalId.Select(x => new NodeLocalId(x))), new NodePath(request.TypeGlobalId.Select(x => new NodeLocalId(x))));
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpPut("edges/type")]
    public async Task<ActionResult<SubgraphResponse>> ChangeEdgeTypeAsync([FromBody] ChangeEdgeTypeRequest request)
    {
        var result = await graph.ChangeEdgeTypeAsync(
            new NodePath(request.Node1InternalId.Select(x => new NodeLocalId(x))),
            new NodePath(request.Node2InternalId.Select(x => new NodeLocalId(x))),
            new NodePath(request.TypeGlobalId.Select(x => new NodeLocalId(x))));
        return ToActionResult<Subgraph, SubgraphResponse>(result, GraphResponseMapper.ToSubgraphResponse);
    }

    [HttpGet("types")]
    public async Task<ActionResult<TypeCatalogResponse>> GetTypesAsync()
    {
        var result = await graph.GetTypeDefinitionsAsync().ConfigureAwait(false);
        return ToActionResult<IReadOnlyCollection<NodeTypeDefinition>, TypeCatalogResponse>(
            result,
            GraphResponseMapper.ToTypeCatalogResponse);
    }

    [HttpPost("types")]
    public async Task<ActionResult<TypeCatalogResponse>> GetTypesAsync([FromBody] TypeCatalogRequest request)
    {
        var roots = (request.Roots ?? Array.Empty<IReadOnlyCollection<string>>())
            .Select(static root => new NodePath(root.Select(static segment => new NodeLocalId(segment))));
        var result = await graph.GetTypeDefinitionsAsync(roots).ConfigureAwait(false);
        return ToActionResult<IReadOnlyCollection<NodeTypeDefinition>, TypeCatalogResponse>(
            result,
            GraphResponseMapper.ToTypeCatalogResponse);
    }
}
