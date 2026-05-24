using GraphData.Core.Services;

namespace GraphData.Api.Services;

internal static class NodePath
{
    public static bool TryJoin(
        IReadOnlyCollection<string>? path,
        string subject,
        out string nodeName,
        out string error)
    {
        if (path is null || path.Count == 0)
        {
            nodeName = string.Empty;
            error = $"{subject} must be provided.";
            return false;
        }

        foreach (var segment in path)
        {
            if (!NodeNameValidator.TryValidateSegment(segment, $"{subject} segment", out error))
            {
                nodeName = string.Empty;
                return false;
            }
        }

        nodeName = string.Join('/', path);
        error = string.Empty;
        return true;
    }
}
