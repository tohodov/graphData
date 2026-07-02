using GraphData.Mcp.Tools;
using Microsoft.Extensions.Configuration;

namespace GraphData.Mcp.Runtime;

public static class McpToolProfileSelector {
    public const string EnvironmentVariableName = "GRAPHDATA_MCP_TOOL_PROFILE";

    public static McpToolProfile Read(IConfiguration configuration) {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName)
            ?? configuration["Mcp:ToolProfile"]
            ?? nameof(McpToolProfile.Semantic);

        if (Enum.TryParse<McpToolProfile>(value, ignoreCase: true, out var profile))
            return profile;

        var validValues = string.Join(", ", Enum.GetNames<McpToolProfile>());
        throw new InvalidOperationException($"Unknown MCP tool profile '{value}'. Valid values: {validValues}.");
    }

    public static Type[] GetToolTypes(McpToolProfile profile) {
        return profile switch {
            McpToolProfile.Semantic => [typeof(GraphDataSemanticTools)],
            McpToolProfile.Raw => [typeof(GraphDataRawTools)],
            McpToolProfile.All => [typeof(GraphDataTools)],
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };
    }
}
