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
            if (controller == typeof(GraphController))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status501NotImplemented,
                    Title = "Legacy graph API is unavailable in semantic mode.",
                    Detail = "Use /api/typed-graph. Raw nodes, edges and topology are available only in legacy mode."
                }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
