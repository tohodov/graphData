using System.ComponentModel;
using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataTools(
    IGraphStorage storage,
    GraphData.Core.Services.IncrementalGraphExpansionService expansionService,
    GraphData.Core.Services.GraphSearchService searchService) {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };

    private readonly IGraphStorage _storage = storage;
    private readonly GraphData.Core.Services.IncrementalGraphExpansionService _expansionService = expansionService;
    private readonly GraphData.Core.Services.GraphSearchService _searchService = searchService;

    [McpServerTool]
    [Description("Gets a graph node by name and returns its attributes and connected node names.")]
    public async Task<string> GetNode(
        [Description("Node name to look up.")] string name) {
        var node = await _storage.Get(name);
        if (node is null)
            return ToJson(new { found = false, name });
        return ToJson(new {
            found = true,
            node = ToResponse(node)
        });
    }

    [McpServerTool]
    [Description("Creates a graph node, optionally under an existing parent node, with optional string attributes.")]
    public async Task<string> CreateNode(
        [Description("Name for the new node.")] string name,
        [Description("Optional parent node name. Leave empty to create a root node.")] string? parentName = null,
        [Description("Optional string attributes for the node.")] Dictionary<string, string>? attributes = null) {
        Node? parent = null;
        if (!string.IsNullOrWhiteSpace(parentName)) {
            parent = await _storage.Get(parentName);
            if (parent is null)
                return ToJson(new { success = false, error = $"Parent node '{parentName}' was not found." });
        }
        var node = await _storage.Create(name, parent, attributes);
        return ToJson(new {
            success = true,
            node = ToResponse(node)
        });
    }

    [McpServerTool]
    [Description("Replaces all attributes for an existing graph node.")]
    public async Task<string> UpdateNodeAttributes(
        [Description("Name of the node to update.")] string name,
        [Description("Complete replacement set of string attributes.")] Dictionary<string, string> attributes) {
        var node = await _storage.Get(name);
        if (node is null)
            return ToJson(new { success = false, error = $"Node '{name}' was not found." });
        await _storage.Update(name, attributes);
        var updated = await _storage.Get(name);
        return ToJson(new {
            success = true,
            node = updated is null ? null : ToResponse(updated)
        });
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("Name of the first node.")] string sourceName,
        [Description("Name of the second node.")] string targetName) {
        if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
            return ToJson(new { success = false, error = "Source and target nodes must be different." });
        var source = await _storage.Get(sourceName);
        var target = await _storage.Get(targetName);
        if (source is null || target is null) {
            return ToJson(new {
                success = false,
                missingNodes = new[] {
                    source is null ? sourceName : null,
                    target is null ? targetName : null
                }.Where(static value => value is not null)
            });
        }
        await _storage.Connect(source, target);
        return ToJson(new {
            success = true,
            source = sourceName,
            target = targetName
        });
    }

    [McpServerTool]
    [Description("Returns a subgraph discovered from root node names up to the requested depth.")]
    public async Task<string> GetSubgraph(
        [Description("Root node names for graph traversal.")] string[] rootNodeIds,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1,
        [Description("Whether disconnected roots should be included when supported by the storage provider.")] bool includeDisconnectedRoots = false) {
        if (maxDepth < 0)
            return ToJson(new { success = false, error = "Max depth must be non-negative." });
        var query = new SubgraphQuery {
            RootNodeIds = rootNodeIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            MaxDepth = maxDepth,
            IncludeDisconnectedRoots = includeDisconnectedRoots
        };
        var subgraph = await _storage.GetSubgraphAsync(query);
        return ToJson(new {
            success = true,
            nodes = subgraph.Nodes.Select(ToResponse).ToArray()
        });
    }

    [McpServerTool]
    [Description("Searches graph nodes by text and structural graph patterns such as descendants of a node and nodes connected to all/any anchor nodes.")]
    public async Task<string> SearchNodes(
        [Description("Optional text to match against node names and string attributes.")] string? text = null,
        [Description("Optional ancestor node name. Results must be below this node in path hierarchy, for example 'weapons/pistols/...'.")] string? descendantOf = null,
        [Description("Maximum hierarchy depth below descendantOf.")] int descendantMaxDepth = 8,
        [Description("Optional anchor node names. Results must be connected to every listed anchor within connectedToAllMaxDepth.")] string[]? connectedToAll = null,
        [Description("Maximum graph distance for connectedToAll anchors.")] int connectedToAllMaxDepth = 2,
        [Description("Optional anchor node names. Results must be connected to at least one listed anchor within connectedToAnyMaxDepth.")] string[]? connectedToAny = null,
        [Description("Maximum graph distance for connectedToAny anchors.")] int connectedToAnyMaxDepth = 2,
        [Description("Maximum number of matches to return.")] int limit = 50) {
        if (descendantMaxDepth < 0 || connectedToAllMaxDepth < 0 || connectedToAnyMaxDepth < 0)
            return ToJson(new { success = false, error = "Depth values must be non-negative." });

        var query = new NodeSearchQuery {
            Text = text,
            DescendantOf = string.IsNullOrWhiteSpace(descendantOf)
                ? null
                : new HierarchySearchPattern {
                    NodeName = descendantOf,
                    MaxDepth = descendantMaxDepth
                },
            ConnectedToAll = (connectedToAll ?? Array.Empty<string>())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new ConnectionSearchPattern {
                    NodeName = name,
                    MaxDepth = connectedToAllMaxDepth
                })
                .ToArray(),
            ConnectedToAny = (connectedToAny ?? Array.Empty<string>())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new ConnectionSearchPattern {
                    NodeName = name,
                    MaxDepth = connectedToAnyMaxDepth
                })
                .ToArray(),
            Limit = limit
        };

        try {
            var matches = await _searchService.SearchNodesAsync(query);
            return ToJson(new {
                success = true,
                matches = matches.Select(static match => new {
                    node = ToResponse(match.Node),
                    score = match.Score,
                    matchedBy = match.MatchedBy
                }).ToArray()
            });
        } catch (ArgumentException ex) {
            return ToJson(new { success = false, error = ex.Message });
        } catch (NotSupportedException ex) {
            return ToJson(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Applies one incremental knowledge-graph expansion step (upsert nodes, connect nodes, and return refreshed context subgraph).")]
    public async Task<string> ApplyIncrementalExpansionStep(
        [Description("Expansion payload containing root context nodes, node upserts, and new connections.")] IncrementalExpansionRequest request) {
        if (request.RootContextNodes.Length == 0)
            return ToJson(new { success = false, error = "At least one root context node is required." });
        var result = await _expansionService.ApplyStepAsync(request);
        return ToJson(new {
            success = true,
            createdNodes = result.CreatedNodes,
            updatedNodes = result.UpdatedNodes,
            connectedPairs = result.ConnectedPairs,
            contextNodes = result.ContextSubgraph.Nodes.Select(ToResponse).ToArray()
        });
    }
    private static object ToResponse(Node node) {
        return new {
            name = node.Name,
            attributes = node.Attributes,
            connectedNodes = node.Nodes
                .Select(static connected => connected.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name)
                .ToArray()
        };
    }

    private static string ToJson(object value) {
        return JsonSerializer.Serialize(value, JsonOptions);
    }
}
