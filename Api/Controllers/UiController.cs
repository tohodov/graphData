using GraphData.Api.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/ui")]
public sealed class UiController(GraphService graph) : ControllerBase {
    [HttpGet("settings")]
    public ActionResult<UiSettingsResponse> GetSettings() =>
        new UiSettingsResponse {
            SystemNodeIds = new UiSystemNodeIdsResponse {
                GraphDataRoot = graph.GraphDataRoot.GlobalId.ToString(),
                TypeRoot = graph.TypeRoot.GlobalId.ToString(),
                NodeTypeRoot = graph.TypesRoot.GlobalId.ToString(),
                StorageRoot = graph.StorageRoot.GlobalId.ToString(),
                InitializerRoot = graph.InitializersRoot.GlobalId.ToString(),
                RuntimeTypesInitializer = graph.RuntimeTypesInitializer.GlobalId.ToString()
            },
            Basis = new UiBasisResponse {
                NodeTypeRoot = graph.TypesRoot.GlobalId.ToString()
            }
        };
}
