using GraphData.Api.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/ui")]
public sealed class UiController(GraphFactory graphFactory) : ControllerBase {
    [HttpGet("settings")]
    public async Task<ActionResult<UiSettingsResponse>> GetSettings() {
        var graph = await graphFactory.OpenAsync().ConfigureAwait(false);
        return new UiSettingsResponse {
            SystemNodeIds = new UiSystemNodeIdsResponse {
                NodeTypeRoot = graph.NodeTypes.GlobalId.ToString(),
                StorageRoot = graph.Root.GlobalId.ToString(),
            },
            Basis = new UiBasisResponse {
                NodeTypeRoot = graph.NodeTypes.GlobalId.ToString()
            }
        };
    }
}
