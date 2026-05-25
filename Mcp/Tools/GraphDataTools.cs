using System.ComponentModel;
using System.Text.Json;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Models;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataTools(GraphApiService graphApi) {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly GraphApiService _graphApi = graphApi;

    [McpServerTool]
    [Description("Gets a graph node by path and returns the same node shape as the HTTP API: attributes plus edges.")]
    public async Task<string> GetNode(
        [Description("Root-relative node path segments to look up.")] string[] path) {
        var result = await _graphApi.GetNodeAsync((NodePath)path);
        if (result.Status is GraphApiStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = result.Value
            });
        }

        if (result.Status is GraphApiStatus.NotFound)
            return ToJson(new { found = false, path });

        return ToJson(new {
            found = false,
            error = GetError(result.Status, result.Error)
        });
    }

    [McpServerTool]
    [Description("Creates a graph node, optionally under an existing parent node, with optional string attributes.")]
    public async Task<string> CreateNode(
        [Description("Local id for the new node.")] string name,
        [Description("Optional parent node path segments. Leave empty to create a root node.")] string[]? parentPath = null,
        [Description("Optional string attributes for the node.")] Dictionary<string, string>? attributes = null) {
        var result = await _graphApi.CreateNodeAsync(new CreateNodeRequest {
            Name = name,
            ParentPath = parentPath,
            Attributes = attributes
        });

        return ToMutationJson(result, "node");
    }

    [McpServerTool]
    [Description("Replaces all attributes for an existing graph node.")]
    public async Task<string> UpdateNodeAttributes(
        [Description("Root-relative path segments of the node to update.")] string[] path,
        [Description("Complete replacement set of string attributes.")] Dictionary<string, string> attributes) {
        var result = await _graphApi.UpdateNodeAsync((NodePath)path, new UpdateNodeRequest {
            Attributes = attributes
        });

        return ToMutationJson(result, "node");
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by path.")]
    public async Task<string> DeleteNode(
        [Description("Root-relative path segments of the node to delete.")] string[] path) {
        var result = await _graphApi.DeleteNodeAsync((NodePath)path);
        return result.Status == GraphApiStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("Root-relative path segments of the first node.")] string[] sourcePath,
        [Description("Root-relative path segments of the second node.")] string[] targetPath) {
        var result = await _graphApi.ConnectNodesAsync(new ConnectNodesRequest {
            SourcePath = (NodePath)sourcePath,
            TargetPath = (NodePath)targetPath
        });

        return result.Status != GraphApiStatus.Ok
            ? ToJson(new {
                success = true,
                sourcePath,
                targetPath
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Returns the same subgraph shape as the HTTP API: nodes plus top-level edges.")]
    public async Task<string> GetSubgraph(
        [Description("Root node path segments for graph traversal.")] string[][] rootPaths,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1) {
        var result = await _graphApi.GetSubgraphAsync(new SubgraphRequest {
            Nodes = rootPaths,
            MaxDepth = maxDepth
        });

        if (result.Status != GraphApiStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        return ToJson(new {
            success = true,
            subgraph = result.Value,
            nodes = result.Value.Nodes,
            edges = result.Value.Edges
        });
    }

    [McpServerTool]
    [Description("Searches graph nodes with a constraint JSON query. The query returns variable bindings that satisfy predicates such as node, text, attribute, connected, path, descendant, degree, any/all/not/exists.")]
    public async Task<string> SearchNodes(
        [Description("Constraint query. Example: {\"return\":[\"n\"],\"where\":{\"kind\":\"all\",\"expressions\":[{\"kind\":\"connected\",\"left\":{\"kind\":\"var\",\"name\":\"n\"},\"right\":{\"kind\":\"var\",\"name\":\"x\"}},{\"kind\":\"attribute\",\"node\":{\"kind\":\"var\",\"name\":\"x\"},\"key\":\"id\",\"operator\":\"equals\",\"value\":\"Y\"}]},\"limit\":50}")] NodeSearchQuery query) {
        if (query is null)
            return ToJson(new { success = false, error = "Query must be provided." });

        var result = await _graphApi.SearchNodesAsync(query);
        return result.Status == GraphApiStatus.Ok && result.Value is not null
            ? ToJson(new {
                success = true,
                matches = result.Value
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    private static string ToMutationJson<T>(GraphApiResponse<T> result, string valuePropertyName) {
        if (result.Status != GraphApiStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        return ToJson(new Dictionary<string, object?> {
            ["success"] = true,
            [valuePropertyName] = result.Value
        });
    }

    private static object ToErrorResponse(GraphApiStatus status, string? error) {
        return new {
            success = false,
            status = status.ToString(),
            error = GetError(status, error)
        };
    }

    private static string GetError(GraphApiStatus status, string? error) {
        if (!string.IsNullOrWhiteSpace(error))
            return error;

        return status switch {
            GraphApiStatus.BadRequest => "Request is invalid.",
            GraphApiStatus.NotFound => "Resource was not found.",
            GraphApiStatus.InternalServerError => "Internal server error.",
            GraphApiStatus.NotImplemented => "Operation is not supported.",
            _ => "Operation failed."
        };
    }

    private static string ToJson(object value) {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        var options = GraphJsonSerializerOptions.Create();
        options.WriteIndented = true;
        return options;
    }
}
