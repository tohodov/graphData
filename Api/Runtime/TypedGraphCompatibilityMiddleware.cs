using GraphData.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace GraphData.Api.Runtime;

/// <summary>Rejects legacy requests before MVC attempts to activate raw graph controllers.</summary>
public sealed class TypedGraphCompatibilityMiddleware(RequestDelegate next, GraphStorageSelection selection)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (selection.Mode == GraphStorageMode.Semantic)
        {
            var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType();
            if (controller == typeof(GraphController) || controller == typeof(UiController))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status501NotImplemented,
                    Title = "Legacy graph API is unavailable in semantic mode.",
                    Detail = "Use /api/typed-graph. Raw nodes, edges, topology and legacy UI settings are available only in legacy mode."
                }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                return;
            }

            if (context.Request.Path == "/" || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>GraphData: semantic mode</title>"
                    + "<body><h1>Включено семантическое хранилище</h1>"
                    + "<p>Текущий редактор работает с графом старого формата и в этом режиме недоступен.</p>"
                    + "<p>Типизированный API: <a href=\"/api/typed-graph/types\">/api/typed-graph/types</a>.</p>"
                    + "<p>Для прежнего редактора выберите GraphStorage:Mode = legacy.</p></body></html>",
                    context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
