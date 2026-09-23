using GraphData.Api.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/ui")]
public sealed class UiController(Graph? graph = null) : ControllerBase {
    [HttpGet("settings")]
    public ActionResult<UiSettingsResponse> GetSettings() {
        return new UiSettingsResponse {
            GraphApi = graph is null ? "typed" : "raw",
            SystemNodeIds = new UiSystemNodeIdsResponse {
                NodeTypeRoot = graph?.NodeTypes.GlobalId.ToString() ?? "",
                StorageRoot = graph?.Root.GlobalId.ToString() ?? "",
            },
            Basis = new UiBasisResponse {
                NodeTypeRoot = graph?.NodeTypes.GlobalId.ToString() ?? ""
            }
        };
    }
}
