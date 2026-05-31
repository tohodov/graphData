using System.ComponentModel;
using System.Text.Json;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataTools(IGraphStorage storage, GraphSearchService searchService) {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IGraphStorage _storage = storage;
    private readonly GraphSearchService _searchService = searchService;

    [McpServerTool]
    [Description("Gets a graph node by path and returns the same node shape as the HTTP API: attributes plus edges.")]
    public async Task<string> GetNode(
        [Description("Root-relative node path segments to look up.")] string[] path) {
        var result = await _storage.Get(new NodeGlobalId(path));
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = result.Value
            });
        }

        if (result.Status is ServiceResultStatus.NotFound)
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
        var result = await _storage.Create(new NodeLocalId(name), parentPath is null ? null : new NodeGlobalId(parentPath), attributes);

        return ToMutationJson(result, "node");
    }

    [McpServerTool]
    [Description("Replaces all attributes for an existing graph node.")]
    public async Task<string> UpdateNodeAttributes(
        [Description("Root-relative path segments of the node to update.")] string[] path,
        [Description("Complete replacement set of string attributes.")] Dictionary<string, string> attributes) {
        var result = await _storage.Update(new NodeGlobalId(path), attributes);
        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by path.")]
    public async Task<string> DeleteNode(
        [Description("Root-relative path segments of the node to delete.")] string[] path) {
        var result = await _storage.Delete(new NodeGlobalId(path));
        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("Root-relative path segments of the first node.")] string[] sourcePath,
        [Description("Root-relative path segments of the second node.")] string[] targetPath) {
        var result = await _storage.Connect(new NodeGlobalId(sourcePath), new NodeGlobalId(targetPath));

        return result.Status == ServiceResultStatus.Ok
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
        var result = await _storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = rootPaths.Select(static path => new NodeGlobalId(path)).ToArray(),
            MaxDepth = maxDepth
        });

        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        var response = GraphResponseMapper.ToSubgraphResponse(result.Value);
        return ToJson(new {
            success = true,
            subgraph = response,
            nodes = response.Nodes,
            edges = response.Edges
        });
    }

    [McpServerTool]
    [Description("Searches graph nodes with a constraint JSON query. The query returns variable bindings that satisfy predicates such as node, text, attribute, connected, path, descendant, degree, any/all/not/exists.")]
    public async Task<string> SearchNodes(
        [Description("Constraint query. Example: {\"return\":[\"n\"],\"where\":{\"kind\":\"all\",\"expressions\":[{\"kind\":\"connected\",\"left\":{\"kind\":\"var\",\"name\":\"n\"},\"right\":{\"kind\":\"var\",\"name\":\"x\"}},{\"kind\":\"attribute\",\"node\":{\"kind\":\"var\",\"name\":\"x\"},\"key\":\"id\",\"operator\":\"equals\",\"value\":\"Y\"}]},\"limit\":50}")] JsonElement query) {
        if (query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return ToJson(new { success = false, error = "Query must be provided." });

        try {
            var parsedQuery = query.Deserialize<NodeSearchQuery>(JsonOptions);
            if (parsedQuery is null)
                return ToJson(new { success = false, error = "Query must be provided." });

            var matches = new List<NodeSearchMatch>();
            await foreach (var match in _searchService.SearchNodesStreamAsync(parsedQuery))
                matches.Add(match);

            return ToJson(new {
                success = true,
                matches = matches.Select(GraphResponseMapper.ToSearchMatchResponse).ToArray()
            });
        } catch (JsonException ex) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Invalid query JSON: {ex.Message}"));
        } catch (NotSupportedException ex) when (IsJsonQueryError(ex)) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Invalid query JSON: {ex.Message}"));
        } catch (ArgumentException ex) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, ex.Message));
        } catch (NotSupportedException ex) {
            return ToJson(ToErrorResponse(ServiceResultStatus.InternalServerError, ex.Message));
        }
    }

    private static string ToMutationJson<T>(ServiceResult<T> result, string valuePropertyName) {
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        return ToJson(new Dictionary<string, object?> {
            ["success"] = true,
            [valuePropertyName] = result.Value
        });
    }

    private static object ToErrorResponse(ServiceResultStatus status, string? error) {
        return new {
            success = false,
            status = status.ToString(),
            error = GetError(status, error)
        };
    }

    private static string GetError(ServiceResultStatus status, string? error) {
        if (!string.IsNullOrWhiteSpace(error))
            return error;

        return status switch {
            ServiceResultStatus.BadRequest => "Request is invalid.",
            ServiceResultStatus.NotFound => "Resource was not found.",
            ServiceResultStatus.InternalServerError => "Internal server error.",
            _ => "Operation failed."
        };
    }

    private static string ToJson(object value) {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static bool IsJsonQueryError(NotSupportedException ex) {
        return ex.Message.Contains("JSON payload", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Json", StringComparison.OrdinalIgnoreCase)
            || ex.StackTrace?.Contains("System.Text.Json", StringComparison.Ordinal) == true;
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        var options = GraphJsonSerializerOptions.Create();
        options.WriteIndented = true;
        return options;
    }
}
