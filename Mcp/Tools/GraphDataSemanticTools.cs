using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataSemanticTools(GraphDataTools tools) {
    private readonly GraphDataTools tools = tools;

    [McpServerTool]
    [Description("Gets a semantic graph node and returns its id plus declared TypeRefs and their effective required-type closure.")]
    public Task<string> GetNode(
        [Description("NodePath segments to look up.")] string[] path) {
        return tools.GetSemanticNode(path);
    }

    [McpServerTool]
    [Description("Creates a canonical typed relation with named members. Member values are slash-separated participant ids or arrays of ids; n-ary and higher-order relations are supported.")]
    public Task<string> CreateRelation(
        [Description("Stable local id for the relation instance.")] string localId,
        [Description("Slash-separated edge type id, for example NodeTypes/Shipment.")] string type,
        [Description("JSON object mapping member names to one participant id or an array of participant ids.")] JsonElement members) {
        return tools.CreateRelation(localId, type, members);
    }

    [McpServerTool]
    [Description("Creates a typed graph node, optionally under an existing parent node. Primitive fields are validated before creation; node-valued fields must be modeled with CreateRelation.")]
    public Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Required type node path, formatted as slash-separated graph path, for example NodeTypes/AssaultRifle.")] string type,
        [Description("Optional parent NodePath segments. Leave empty to create a root node.")] string[]? path = null,
        [Description("Optional JSON object whose keys are primitive type field names. Node-valued fields are rejected before mutation; model links with an explicit Edge type and CreateRelation.")] JsonElement? fields = null) {
        return tools.CreateNode(localId, path, type, fields);
    }

    [McpServerTool]
    [Description("Creates a dynamic node or relation type under the fixed type catalog.")]
    public Task<string> CreateType(
        [Description("LocalId for the new type node under NodeTypes.")] string localId,
        [Description("Whether this type is abstract.")] bool isAbstract = false,
        [Description("Optional JSON object or array of field definitions. Node fields use nodeType paths; primitive fields use clrType names such as string, int, bool, decimal.")] JsonElement? fields = null,
        [Description("Optional JSON object or array of slot definitions. Each slot must specify allowedTypes or allowedType with existing type paths.")] JsonElement? slots = null,
        [Description("Optional slash-separated paths of types required by the new type.")] string[]? requires = null,
        [Description("Semantic element kind: Node or Edge. Edge creates a relation type.")] string elementKind = "Node") {
        return tools.CreateType(localId, isAbstract, fields, slots, requires, elementKind);
    }

    [McpServerTool]
    [Description("Returns definitions for all currently known graph node types.")]
    public Task<string> GetTypeDefinitions() {
        return tools.GetTypeDefinitions();
    }

}
