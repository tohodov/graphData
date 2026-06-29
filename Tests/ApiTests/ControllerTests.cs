using Abstractions;
using GraphData.Api.Controllers;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public class ControllerTests : GraphServiceTests {
    GraphController? controller;

    protected GraphController Controller => controller ??= CreateController(Service);

    internal static GraphController CreateController(GraphService service) {
        var controller = new GraphController(service);
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    internal static GraphController CreateController(IGraphStorage storage) { //TODO сделать virtual
        var controller = new GraphController(new GraphService(storage, new GraphSearchService(storage), new CancellationTokensAccessorMock(), GraphSchemaRegistry.Create()));
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }
}
