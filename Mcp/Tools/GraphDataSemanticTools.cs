using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataSemanticTools(GraphDataTools tools) {
    private readonly GraphDataTools tools = tools;

    [McpServerTool]
    [Description("Gets a semantic graph node and returns its backing LocalId, InternalId, and materialized or inferred types.")]
    public Task<string> GetNode(
        [Description("NodePath segments to look up.")] string[] path,
        [Description("Optional slash-separated paths of nodes selected as the type basis. Omit to use the default NodeTypes catalog.")] string[]? basisTypes = null) {
        return tools.GetSemanticNode(path, basisTypes);
    }

    [McpServerTool]
    [Description("Creates a typed graph node, optionally under an existing parent node. Fields are validated against the type definition before creation.")]
    public Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Required type node path, formatted as slash-separated graph path, for example NodeTypes/AssaultRifle.")] string type,
        [Description("Optional parent NodePath segments. Leave empty to create a root node.")] string[]? path = null,
        [Description("Optional JSON object whose keys are type field names. Node fields must reference existing nodes by path; missing nodes are not created.")] JsonElement? fields = null) {
        return tools.CreateNode(localId, path, type, fields);
    }

    [McpServerTool]
    [Description("Creates a dynamic graph node type under NodeTypes. Field, slot, and required type references must point to existing type nodes; missing types are not created.")]
    public Task<string> CreateType(
        [Description("LocalId for the new type node under NodeTypes.")] string localId,
        [Description("Whether this type is abstract.")] bool isAbstract = false,
        [Description("Optional JSON object or array of field definitions. Node fields use nodeType paths; primitive fields use clrType names such as string, int, bool, decimal.")] JsonElement? fields = null,
        [Description("Optional JSON object or array of slot definitions. Each slot must specify allowedTypes or allowedType with existing type paths.")] JsonElement? slots = null,
        [Description("Optional slash-separated paths of node types required by the new type.")] string[]? requires = null) {
        return tools.CreateType(localId, isAbstract, fields, slots, requires);
    }

    [McpServerTool]
    [Description("Returns definitions for all currently known graph node types.")]
    public Task<string> GetTypeDefinitions() {
        return tools.GetTypeDefinitions();
    }

}
