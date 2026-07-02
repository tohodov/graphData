using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataSemanticTools(GraphDataTools tools) {
    private readonly GraphDataTools tools = tools;

    [McpServerTool]
    [Description("Gets a graph node by GlobalId and returns its LocalId, GlobalId, and edges.")]
    public Task<string> GetNode(
        [Description("NodePath segments to look up.")] string[] path) {
        return tools.GetNode(path);
    }

    [McpServerTool]
    [Description("Creates a graph node, optionally under an existing parent node. When type is provided, fields are validated against the type definition before creation.")]
    public Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Optional parent NodePath segments. Leave empty to create a root node.")] string[]? path = null,
        [Description("Optional type node path, formatted as slash-separated graph path, for example NodeTypes/AssaultRifle.")] string? type = null,
        [Description("Optional JSON object whose keys are type field names. Node fields must reference existing nodes by path; missing nodes are not created.")] JsonElement? fields = null) {
        return tools.CreateNode(localId, path, type, fields);
    }

    [McpServerTool]
    [Description("Creates a dynamic graph node type under NodeTypes. Field and slot nodeType references must point to existing type nodes; missing types are not created.")]
    public Task<string> CreateType(
        [Description("LocalId for the new type node under NodeTypes.")] string localId,
        [Description("Whether this type is abstract.")] bool isAbstract = false,
        [Description("Optional JSON object or array of field definitions. Node fields use nodeType paths; primitive fields use clrType names such as string, int, bool, decimal.")] JsonElement? fields = null,
        [Description("Optional JSON object or array of slot definitions. Each slot must specify allowedTypes or allowedType with existing type paths.")] JsonElement? slots = null) {
        return tools.CreateType(localId, isAbstract, fields, slots);
    }

    [McpServerTool]
    [Description("Returns definitions for all currently known graph node types.")]
    public Task<string> GetTypeDefinitions() {
        return tools.GetTypeDefinitions();
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
