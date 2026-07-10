using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataRawTools(GraphDataTools tools) {
    private readonly GraphDataTools tools = tools;

    [McpServerTool]
    [Description("Gets a graph node by GlobalId and returns its LocalId, GlobalId, and edges.")]
    public Task<string> GetNode(
        [Description("NodePath segments to look up.")] string[] path) {
        return tools.GetNode(path);
    }

    [McpServerTool]
    [Description("Creates an untyped graph node, optionally under an existing parent node.")]
    public Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Optional parent NodePath segments. Leave empty to create a root node.")] string[]? path = null) {
        return tools.CreateNode(localId, path);
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by GlobalId.")]
    public Task<string> DeleteNode(
        [Description("NodePath segments of the node to delete.")] string[] path) {
        return tools.DeleteNode(path);
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public Task<string> ConnectNodes(
        [Description("NodePath segments of the first node.")] string[] sourceGlobalId,
        [Description("NodePath segments of the second node.")] string[] targetGlobalId) {
        return tools.ConnectNodes(sourceGlobalId, targetGlobalId);
    }

    [McpServerTool]
    [Description("Removes an existing undirected connection between two graph nodes without deleting either node.")]
    public Task<string> DisconnectNodes(
        [Description("NodePath segments of the first node.")] string[] sourceGlobalId,
        [Description("NodePath segments of the second node.")] string[] targetGlobalId) {
        return tools.DisconnectNodes(sourceGlobalId, targetGlobalId);
    }

    [McpServerTool]
    [Description("Returns a subgraph with nodes plus top-level edges.")]
    public Task<string> GetSubgraph(
        [Description("Root nodes NodePath segments for graph traversal.")] string[][] rootPaths,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1) {
        return tools.GetSubgraph(rootPaths, maxDepth);
    }

    [McpServerTool]
    [Description("Searches graph nodes with a constraint JSON query. The query returns variable bindings that satisfy predicates such as node, text, connected, descendant, degree, any/all/not/exists.")]
    public Task<string> SearchNodes(
        [Description("Constraint query. Example: {\"return\":[\"n\"],\"where\":{\"kind\":\"all\",\"expressions\":[{\"kind\":\"node\",\"node\":{\"kind\":\"var\",\"name\":\"n\"}},{\"kind\":\"text\",\"node\":{\"kind\":\"var\",\"name\":\"n\"},\"operator\":\"contains\",\"value\":\"Weapon\"}]},\"limit\":50}")] JsonElement query) {
        return tools.SearchNodes(query);
    }
}
