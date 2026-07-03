using Abstractions;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

public abstract class GraphControllerBase : ControllerBase
{
    protected ActionResult<TO> ToActionResult<FROM, TO>(ServiceResult<FROM> result, Func<FROM, TO> map)
    {
        return result.Status switch
        {
            ServiceResultStatus.Ok when result.Value is not null => Ok(map(result.Value)),
            ServiceResultStatus.BadRequest => BadRequest(result.Error),
            ServiceResultStatus.NotFound => NotFound(),
            ServiceResultStatus.Conflict => Conflict(result.Error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    protected ActionResult<T> ToActionResult<T>(ServiceResult result)
    {
        return ToActionResult<T>(result.Status, result.Error);
    }

    protected ActionResult<T> ToActionResult<T>(ServiceResultStatus status, string? error)
    {
        return status switch
        {
            ServiceResultStatus.Ok => Ok(),
            ServiceResultStatus.BadRequest => BadRequest(error),
            ServiceResultStatus.NotFound => NotFound(),
            ServiceResultStatus.Conflict => Conflict(error),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    protected static bool TryCreateRequiredNodePath(
        string[] globalId,
        string[] path,
        out NodePath nodePath,
        out string? error)
    {
        var segments = globalId.Length > 0
            ? globalId
            : path;
        if (segments.Length == 0)
        {
            nodePath = new NodePath();
            error = "Node globalId is required.";
            return false;
        }

        nodePath = new NodePath(segments);
        error = null;
        return true;
    }
}
