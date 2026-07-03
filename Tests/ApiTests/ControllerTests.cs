using Abstractions;
using GraphData.Api.Controllers;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public class ControllerTests : GraphServiceTests {
    GraphController? controller;
    SemanticGraphController? semanticController;

    protected GraphController Controller => controller ??= CreateController(Service);
    protected SemanticGraphController SemanticController => semanticController ??= CreateSemanticController(Service);

    internal static GraphController CreateController(GraphService service) {
        var controller = new GraphController(service);
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    internal static SemanticGraphController CreateSemanticController(GraphService service) {
        var controller = new SemanticGraphController(service);
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    internal static async Task<GraphController> CreateController(IGraphStorage storage) { //TODO сделать virtual
        var graph = await Graph.OpenAsync(storage, GraphSchemaRegistry.Create());
        var controller = new GraphController(new GraphService(graph, new GraphSearchService(storage)));
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }
}
