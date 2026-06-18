using GraphData.Api.Models;
using GraphData.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/ui")]
public sealed class UiController : ControllerBase
{
    [HttpGet("settings")]
    public ActionResult<UiSettingsResponse> GetSettings() =>
        new UiSettingsResponse
        {
            SystemNodeIds = new UiSystemNodeIdsResponse
            {
                GraphDataRoot = GraphSystemNodeIds.GraphDataRoot.ToString(),
                TypeRoot = GraphSystemNodeIds.TypeRoot.ToString(),
                NodeTypeRoot = GraphSystemNodeIds.NodeTypeRoot.ToString(),
                EdgeTypeRoot = GraphSystemNodeIds.EdgeTypeRoot.ToString(),
                RelationRoot = GraphSystemNodeIds.RelationRoot.ToString(),
                StorageRoot = GraphSystemNodeIds.StorageRoot.ToString(),
                InitializerRoot = GraphSystemNodeIds.InitializerRoot.ToString(),
                RuntimeTypesInitializer = GraphSystemNodeIds.RuntimeTypesInitializer.ToString()
            },
            BaseTypeIds = new UiBaseTypeIdsResponse
            {
                NodeType = GraphBaseTypeIds.NodeType.ToString(),
                NodeInstance = GraphBaseTypeIds.NodeInstance.ToString(),
                EdgeType = GraphBaseTypeIds.EdgeType.ToString(),
                EdgeInstance = GraphBaseTypeIds.EdgeInstance.ToString()
            },
            Basis = new UiBasisResponse
            {
                NodeTypeRoot = GraphSystemNodeIds.NodeTypeRoot.ToString(),
                EdgeTypeRoot = GraphSystemNodeIds.EdgeTypeRoot.ToString(),
                RelationRoot = GraphSystemNodeIds.RelationRoot.ToString()
            }
        };
}
