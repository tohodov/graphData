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
    [Description("Gets a graph node by GlobalId and returns the same node shape as the HTTP API: LocalId, GlobalId, attributes, and edges.")]
    public async Task<string> GetNode(
        [Description("GlobalId segments to look up.")] string[] globalId) {
        var result = await _storage.Get(new NodeGlobalId(globalId));
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = GraphResponseMapper.ToNodeResponse(new Node(result.Value))
            });
        }

        if (result.Status is ServiceResultStatus.NotFound)
            return ToJson(new { found = false, globalId });

        return ToJson(new {
            found = false,
            error = GetError(result.Status, result.Error)
        });
    }

    [McpServerTool]
    [Description("Creates a graph node, optionally under an existing parent node, with optional string attributes.")]
    public async Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Optional parent GlobalId segments. Leave empty to create a root node.")] string[]? parentGlobalId = null,
        [Description("Optional string attributes for the node.")] Dictionary<string, string>? attributes = null) {
        var result = await _storage.Create(new NodeLocalId(localId), parentGlobalId is null ? null : new NodeGlobalId(parentGlobalId), attributes);

        return ToMutationJson(result, "node", static state => GraphResponseMapper.ToNodeResponse(new Node(state)));
    }

    [McpServerTool]
    [Description("Replaces all attributes for an existing graph node.")]
    public async Task<string> UpdateNodeAttributes(
        [Description("GlobalId segments of the node to update.")] string[] globalId,
        [Description("Complete replacement set of string attributes.")] Dictionary<string, string> attributes) {
        var result = await _storage.Update(new NodeGlobalId(globalId), attributes);
        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by GlobalId.")]
    public async Task<string> DeleteNode(
        [Description("GlobalId segments of the node to delete.")] string[] globalId) {
        var result = await _storage.Delete(new NodeGlobalId(globalId));
        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("GlobalId segments of the first node.")] string[] sourceGlobalId,
        [Description("GlobalId segments of the second node.")] string[] targetGlobalId) {
        var result = await _storage.Connect(new NodeGlobalId(sourceGlobalId), new NodeGlobalId(targetGlobalId));

        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new {
                success = true,
                sourceGlobalId,
                targetGlobalId
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Returns the same subgraph shape as the HTTP API: nodes plus top-level edges.")]
    public async Task<string> GetSubgraph(
        [Description("Root node GlobalId segments for graph traversal.")] string[][] rootGlobalIds,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1) {
        var result = await _storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = rootGlobalIds.Select(static globalId => new NodeGlobalId(globalId)).ToArray(),
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
    [Description("Searches graph nodes with a constraint JSON query. The query returns variable bindings that satisfy predicates such as node, text, attribute, connected, descendant, degree, any/all/not/exists.")]
    public async Task<string> SearchNodes(
        [Description("Constraint query. Example: {\"return\":[\"n\"],\"where\":{\"kind\":\"all\",\"expressions\":[{\"kind\":\"connected\",\"left\":{\"kind\":\"var\",\"name\":\"n\"},\"right\":{\"kind\":\"var\",\"name\":\"x\"}},{\"kind\":\"attribute\",\"node\":{\"kind\":\"var\",\"name\":\"x\"},\"key\":\"id\",\"operator\":\"equals\",\"value\":\"Y\"}]},\"limit\":50}")] JsonElement query) {
        if (query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return ToJson(new { success = false, error = "Query must be provided." });

        try {
            var parsedRequest = query.Deserialize<NodeSearchQueryRequest>(JsonOptions);
            if (parsedRequest is null)
                return ToJson(new { success = false, error = "Query must be provided." });
            var parsedQuery = GraphRequestMapper.ToNodeSearchQuery(parsedRequest);

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

    private static string ToMutationJson<T>(ServiceResult<T> result, string valuePropertyName, Func<T, object>? map = null) {
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        return ToJson(new Dictionary<string, object?> {
            ["success"] = true,
            [valuePropertyName] = map is null ? result.Value : map(result.Value)
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
            ServiceResultStatus.Conflict => "Request conflicts with the current graph state.",
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
