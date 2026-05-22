using System.ComponentModel;
using System.Text.Json;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Models;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataTools(
    GraphApiService graphApi,
    GraphData.Core.Services.IncrementalGraphExpansionService expansionService) {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly GraphApiService _graphApi = graphApi;
    private readonly GraphData.Core.Services.IncrementalGraphExpansionService _expansionService = expansionService;

    [McpServerTool]
    [Description("Gets a graph node by name and returns the same node shape as the HTTP API: attributes plus edges.")]
    public async Task<string> GetNode(
        [Description("Node name to look up.")] string name) {
        var result = await _graphApi.GetNodeAsync(name);
        if (result.Status is GraphApiStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = result.Value
            });
        }

        if (result.Status is GraphApiStatus.NotFound)
            return ToJson(new { found = false, name });

        return ToJson(new {
            found = false,
            error = GetError(result.Status, result.Error)
        });
    }

    [McpServerTool]
    [Description("Creates a graph node, optionally under an existing parent node, with optional string attributes.")]
    public async Task<string> CreateNode(
        [Description("Name for the new node.")] string name,
        [Description("Optional parent node name. Leave empty to create a root node.")] string? parentName = null,
        [Description("Optional string attributes for the node.")] Dictionary<string, string>? attributes = null) {
        var result = await _graphApi.CreateNodeAsync(new CreateNodeRequest {
            Name = name,
            ParentName = parentName,
            Attributes = attributes
        });

        return ToMutationJson(result, "node");
    }

    [McpServerTool]
    [Description("Replaces all attributes for an existing graph node.")]
    public async Task<string> UpdateNodeAttributes(
        [Description("Name of the node to update.")] string name,
        [Description("Complete replacement set of string attributes.")] Dictionary<string, string> attributes) {
        var result = await _graphApi.UpdateNodeAsync(name, new UpdateNodeRequest {
            Attributes = attributes
        });

        return ToMutationJson(result, "node");
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by name.")]
    public async Task<string> DeleteNode(
        [Description("Name of the node to delete.")] string name) {
        var result = await _graphApi.DeleteNodeAsync(name);
        return result.Succeeded
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("Name of the first node.")] string sourceName,
        [Description("Name of the second node.")] string targetName) {
        var result = await _graphApi.ConnectNodesAsync(new ConnectNodesRequest {
            SourceName = sourceName,
            TargetName = targetName
        });

        return result.Succeeded
            ? ToJson(new {
                success = true,
                source = sourceName,
                target = targetName
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Returns the same subgraph shape as the HTTP API: nodes plus top-level edges.")]
    public async Task<string> GetSubgraph(
        [Description("Root node names for graph traversal.")] string[] rootNodeIds,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1,
        [Description("Whether disconnected roots should be included when supported by the storage provider.")] bool includeDisconnectedRoots = false) {
        var result = await _graphApi.GetSubgraphAsync(new SubgraphRequest {
            RootNodeIds = rootNodeIds,
            MaxDepth = maxDepth,
            IncludeDisconnectedRoots = includeDisconnectedRoots
        });

        if (!result.Succeeded || result.Value is null)
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
        return result.Succeeded && result.Value is not null
            ? ToJson(new {
                success = true,
                matches = result.Value
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Applies one incremental knowledge-graph expansion step (upsert nodes, connect nodes, and return refreshed context subgraph).")]
    public async Task<string> ApplyIncrementalExpansionStep(
        [Description("Expansion payload containing root context nodes, node upserts, and new connections.")] IncrementalExpansionRequest request) {
        if (request.RootContextNodes.Length == 0)
            return ToJson(new { success = false, error = "At least one root context node is required." });
        var result = await _expansionService.ApplyStepAsync(request);
        var contextSubgraph = GraphResponseMapper.ToSubgraphResponse(result.ContextSubgraph);
        return ToJson(new {
            success = true,
            createdNodes = result.CreatedNodes,
            updatedNodes = result.UpdatedNodes,
            connectedPairs = result.ConnectedPairs,
            contextSubgraph,
            contextNodes = contextSubgraph.Nodes,
            contextEdges = contextSubgraph.Edges
        });
    }

    private static string ToMutationJson<T>(GraphApiResponse<T> result, string valuePropertyName) {
        if (!result.Succeeded || result.Value is null)
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
