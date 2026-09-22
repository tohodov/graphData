using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using ModelContextProtocol.Server;

namespace GraphData.Mcp.Tools;

[McpServerToolType]
public sealed class GraphDataTools(GraphService graph, global::Graph schemaGraph) {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly GraphService graph = graph;
    private readonly global::Graph schemaGraph = schemaGraph;

    [McpServerTool]
    [Description("Gets a graph node by GlobalId and returns its LocalId, GlobalId, and edges.")]
    public async Task<string> GetNode(
        [Description("NodePath segments to look up.")] string[] path) {
        var result = await graph.GetNode(new NodePath(path));
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = ToNodeResponse(result.Value)
            });
        }

        if (result.Status is ServiceResultStatus.NotFound)
            return ToJson(new { found = false, path });

        return ToJson(new {
            found = false,
            error = GetError(result.Status, result.Error)
        });
    }

    public async Task<string> GetSemanticNode(string[] path, string[]? basisTypes = null) {
        IEnumerable<NodeRef>? basis = null;
        if (basisTypes is not null) {
            var paths = new List<NodeRef>(basisTypes.Length);
            foreach (var basisType in basisTypes) {
                var basisPath = ParseNodePath(basisType);
                if (basisPath is null)
                    return ToJson(ToErrorResponse(
                        ServiceResultStatus.BadRequest,
                        "Basis type paths cannot be empty."));
                paths.Add(basisPath);
            }
            basis = paths;
        }

        var result = await graph.GetSemanticNodeAsync(new NodePath(path), basis).ConfigureAwait(false);
        if (result.Status is ServiceResultStatus.Ok && result.Value is not null) {
            return ToJson(new {
                found = true,
                node = ToSemanticNodeResponse(result.Value)
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
    [Description("Creates a graph node, optionally under an existing parent node. When type is provided, fields are validated against the type definition before creation.")]
    public async Task<string> CreateNode(
        [Description("LocalId for the new node.")] string localId,
        [Description("Optional parent NodePath segments. Leave empty to create a root node.")] string[]? path = null,
        [Description("Optional type node path, formatted as slash-separated graph path, for example NodeTypes/AssaultRifle.")] string? type = null,
        [Description("Optional JSON object whose keys are type field names. Node fields must reference existing nodes by path; missing nodes are not created.")] JsonElement? fields = null) {
        if (string.IsNullOrWhiteSpace(type)) {
            if (HasJsonValue(fields))
                return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, "Fields require a type."));

            var untypedResult = await graph.CreateNode(localId, (NodePath?)path);
            return ToMutationJson(untypedResult, "node", static node => ToNodeResponse(node));
        }

        var typePath = ParseNodePath(type);
        if (typePath is null)
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, "Type path is required."));

        var typeNode = await graph.GetTypeNode(typePath).ConfigureAwait(false);
        if (typeNode is null)
            return ToJson(ToErrorResponse(ServiceResultStatus.NotFound, $"Type node '{type}' was not found or is not a node type."));

        var effectiveDefinitions = await graph.GetEffectiveTypeDefinitionsAsync(typeNode.GlobalId).ConfigureAwait(false);
        if (effectiveDefinitions.Status != ServiceResultStatus.Ok || effectiveDefinitions.Value is null)
            return ToJson(ToErrorResponse(effectiveDefinitions.Status, effectiveDefinitions.Error));
        var plan = await BuildCreateNodePlan(typeNode, effectiveDefinitions.Value, fields).ConfigureAwait(false);
        if (plan.Status != ServiceResultStatus.Ok || plan.Value is null)
            return ToJson(ToErrorResponse(plan.Status, plan.Error));

        var result = await graph.CreateNode(localId, (NodePath?)path);
        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        var createdPath = (NodePath)result.Value.GlobalId;
        try {
            foreach (var link in plan.Value.NodeLinks) {
                var connect = await graph.ConnectNodesAsync(createdPath, link).ConfigureAwait(false);
                if (connect.Status != ServiceResultStatus.Ok)
                    throw new TypedCreateException(connect.Status, GetError(connect.Status, connect.Error));
            }

            if (plan.Value.PrimitiveAttributes.Count > 0) {
                var update = await graph.UpdateNode(
                    createdPath,
                    new Dictionary<string, string>(plan.Value.PrimitiveAttributes, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
                if (update.Status != ServiceResultStatus.Ok)
                    throw new TypedCreateException(update.Status, GetError(update.Status, update.Error));
            }

            var assign = await graph.AssignNodeTypeAsync(createdPath, typeNode.GlobalId).ConfigureAwait(false);
            if (assign.Status != ServiceResultStatus.Ok)
                throw new TypedCreateException(assign.Status, GetError(assign.Status, assign.Error));

            var reload = await graph.GetSemanticNodeAsync(createdPath).ConfigureAwait(false);
            if (reload.Status != ServiceResultStatus.Ok || reload.Value is null)
                throw new TypedCreateException(reload.Status, GetError(reload.Status, reload.Error));
            return ToMutationJson(reload, "node", static node => ToSemanticNodeResponse(node));
        } catch (TypedCreateException ex) {
            await graph.DeleteNode(createdPath).ConfigureAwait(false);
            return ToJson(ToErrorResponse(ex.Status, $"Typed node creation was rolled back: {ex.Message}"));
        } catch (Exception ex) {
            await graph.DeleteNode(createdPath).ConfigureAwait(false);
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Typed node creation was rolled back: {ex.Message}"));
        }
    }

    [McpServerTool]
    [Description("Creates a dynamic graph node type under NodeTypes. Field, slot, and required type references must point to existing type nodes; missing types are not created.")]
    public async Task<string> CreateType(
        [Description("LocalId for the new type node under NodeTypes.")] string localId,
        [Description("Whether this type is abstract.")] bool isAbstract = false,
        [Description("Optional JSON object or array of field definitions. Node fields use nodeType paths; primitive fields use clrType names such as string, int, bool, decimal.")] JsonElement? fields = null,
        [Description("Optional JSON object or array of slot definitions. Each slot must specify allowedTypes or allowedType with existing type paths.")] JsonElement? slots = null,
        [Description("Optional slash-separated paths of node types required by the new type.")] string[]? requires = null) {
        var fieldDefinitions = await ReadTypeFields(fields).ConfigureAwait(false);
        if (fieldDefinitions.Status != ServiceResultStatus.Ok || fieldDefinitions.Value is null)
            return ToJson(ToErrorResponse(fieldDefinitions.Status, fieldDefinitions.Error));

        var slotDefinitions = await ReadTypeSlots(slots).ConfigureAwait(false);
        if (slotDefinitions.Status != ServiceResultStatus.Ok || slotDefinitions.Value is null)
            return ToJson(ToErrorResponse(slotDefinitions.Status, slotDefinitions.Error));

        var requiredTypes = new List<NodeType>();
        foreach (var requiredTypePath in requires ?? []) {
            var path = ParseNodePath(requiredTypePath);
            if (path is null)
                return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, "Required type path is required."));

            var requiredType = await graph.GetTypeNode(path).ConfigureAwait(false);
            if (requiredType is null)
                return ToJson(ToErrorResponse(
                    ServiceResultStatus.NotFound,
                    $"Required type node '{requiredTypePath}' was not found or is not a node type."));

            requiredTypes.Add(requiredType);
        }

        var result = await graph.CreateNodeType(
            localId,
            isAbstract,
            fieldDefinitions.Value,
            slotDefinitions.Value,
            requiredTypes).ConfigureAwait(false);

        return ToMutationJson(result, "type", static definition => ToTypeDefinitionResponse(definition));
    }

    [McpServerTool]
    [Description("Returns definitions for all currently known graph node types.")]
    public async Task<string> GetTypeDefinitions() {
        var types = await GetKnownTypeDefinitions().ConfigureAwait(false);
        return ToJson(new {
            success = true,
            types
        });
    }

    [McpServerTool]
    [Description("Deletes an existing graph node by GlobalId.")]
    public async Task<string> DeleteNode(
        [Description("NodePath segments of the node to delete.")] string[] path) {
        var result = await graph.DeleteNode(new NodePath(path));
        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new { success = true })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Creates an undirected connection between two existing graph nodes.")]
    public async Task<string> ConnectNodes(
        [Description("NodePath segments of the first node.")] string[] sourceGlobalId,
        [Description("NodePath segments of the second node.")] string[] targetGlobalId) {
        var result = await graph.ConnectNodesAsync(new NodePath(sourceGlobalId), new NodePath(targetGlobalId));

        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new {
                success = true,
                sourceGlobalId,
                targetGlobalId
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Removes an existing undirected connection between two graph nodes without deleting either node.")]
    public async Task<string> DisconnectNodes(
        [Description("NodePath segments of the first node.")] string[] sourceGlobalId,
        [Description("NodePath segments of the second node.")] string[] targetGlobalId) {
        var result = await graph.Disconnect(new NodePath(sourceGlobalId), new NodePath(targetGlobalId));

        return result.Status == ServiceResultStatus.Ok
            ? ToJson(new {
                success = true,
                sourceGlobalId,
                targetGlobalId
            })
            : ToJson(ToErrorResponse(result.Status, result.Error));
    }

    [McpServerTool]
    [Description("Returns a subgraph with nodes plus top-level edges.")]
    public async Task<string> GetSubgraph(
        [Description("Root nodes NodePath segments for graph traversal.")] string[][] rootPaths,
        [Description("Maximum traversal depth. Use 0 to return only roots.")] int maxDepth = 1) {
        var result = await graph.GetSubgraph(rootPaths.Select(x => new NodePath(x)), maxDepth);

        if (result.Status != ServiceResultStatus.Ok || result.Value is null)
            return ToJson(ToErrorResponse(result.Status, result.Error));

        var response = ToSubgraphResponse(result.Value);
        return ToJson(new {
            success = true,
            subgraph = response,
            nodes = response.Nodes,
            edges = response.Edges
        });
    }

    [McpServerTool]
    [Description("Searches graph nodes with a constraint JSON query. The query returns variable bindings that satisfy predicates such as node, text, connected, descendant, degree, any/all/not/exists.")]
    public async Task<string> SearchNodes(
        [Description("Constraint query. Example: {\"return\":[\"n\"],\"where\":{\"kind\":\"all\",\"expressions\":[{\"kind\":\"node\",\"node\":{\"kind\":\"var\",\"name\":\"n\"}},{\"kind\":\"text\",\"node\":{\"kind\":\"var\",\"name\":\"n\"},\"operator\":\"contains\",\"value\":\"Weapon\"}]},\"limit\":50}")] JsonElement query) {
        if (query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return ToJson(new { success = false, error = "Query must be provided." });

        if (ContainsMcpHiddenSearchPredicate(query))
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, "This search predicate is not exposed by the MCP API."));

        try {
            var parsedQuery = query.Deserialize<NodeSearchQuery>(JsonOptions);
            if (parsedQuery is null)
                return ToJson(new { success = false, error = "Query must be provided." });

            var matches = new List<NodeSearchMatch>();
            await foreach (var match in graph.SearchNodesStreamAsync(parsedQuery))
                matches.Add(match);

            return ToJson(new {
                success = true,
                matches = matches.Select(ToSearchMatchResponse).ToArray()
            });
        } catch (JsonException ex) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Invalid query JSON: {ex.Message}"));
        } catch (NotSupportedException ex) when (IsJsonQueryError(ex)) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Invalid query JSON: {ex.Message}"));
        } catch (ArgumentException ex) {
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, ex.Message));
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
            _ => "Operation failed."
        };
    }

    private async Task<IReadOnlyCollection<McpNodeTypeDefinitionResponse>> GetKnownTypeDefinitions() {
        var types = new Dictionary<string, NodeType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in schemaGraph.RuntimeTypes)
            types[type.GlobalId.ToString()] = type;

        foreach (var node in schemaGraph.NodeTypes.Nodes) {
            var type = await graph.GetTypeNode(node).ConfigureAwait(false);
            if (type is not null)
                types[type.GlobalId.ToString()] = type;
        }

        return types.Values
            .OrderBy(static type => type.GlobalId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(type => ToTypeDefinitionResponse(schemaGraph.GetNodeTypeDefinition(type)))
            .ToArray();
    }

    private static McpNodeTypeDefinitionResponse ToTypeDefinitionResponse(NodeTypeDefinition definition) {
        return new McpNodeTypeDefinitionResponse {
            LocalId = definition.Type.LocalId.ToString(),
            InternalId = definition.Type.GlobalId.ToString(),
            IsAbstract = definition.IsAbstract,
            RequiredTypeInternalIds = definition.RequiredTypes
                .Select(static type => type.GlobalId.ToString())
                .ToArray(),
            Fields = definition.Fields.Select(static field => new McpNodeTypeFieldResponse {
                Name = field.Name,
                ValueKind = field.ValueKind.ToString(),
                ClrType = FormatClrType(field.ClrType),
                Cardinality = ToCardinalityResponse(field.Cardinality),
                IsCollection = field.IsCollection,
                NodeTypeInternalId = field.NodeType?.GlobalId.ToString()
            }).ToArray(),
            Slots = definition.Slots.Select(static slot => new McpNodeTypeSlotResponse {
                Name = slot.Name,
                Cardinality = ToCardinalityResponse(slot.Cardinality),
                AllowedTypeInternalIds = slot.AllowedTypes
                    .Select(static type => type.GlobalId.ToString())
                    .ToArray()
            }).ToArray()
        };
    }

    private async Task<ServiceResult<McpCreateNodePlan>> BuildCreateNodePlan(
        NodeType selectedType,
        IReadOnlyCollection<NodeTypeDefinition> effectiveDefinitions,
        JsonElement? fields) {
        var fieldsResult = ReadFieldsObject(fields);
        if (fieldsResult.Status != ServiceResultStatus.Ok || fieldsResult.Value is null)
            return ServiceResult<McpCreateNodePlan>.From(fieldsResult);

        var providedFields = fieldsResult.Value;
        var contractsByType = effectiveDefinitions
            .Select(definition => new {
                Definition = definition,
                Contracts = BuildFieldContracts(definition)
            })
            .ToArray();
        var ambiguousField = contractsByType
            .SelectMany(item => item.Contracts.Keys.Select(name => new {
                Name = name,
                Type = item.Definition.Type
            }))
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (ambiguousField is not null)
            return ServiceResult<McpCreateNodePlan>.BadRequest(
                $"Field '{ambiguousField.Key}' is ambiguous across effective types: {string.Join(", ", ambiguousField.Select(static item => item.Type.GlobalId))}.");

        var contracts = contractsByType
            .SelectMany(static item => item.Contracts)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in providedFields.Keys)
            if (!contracts.ContainsKey(name))
                return ServiceResult<McpCreateNodePlan>.BadRequest(
                    $"Type '{selectedType.GlobalId}' and its required types do not define field '{name}'.");

        var missingFields = contracts.Values
            .Where(contract => !contract.Cardinality.Contains(0) && !providedFields.ContainsKey(contract.Name))
            .Select(FormatRequiredField)
            .ToArray();
        if (missingFields.Length > 0)
            return ServiceResult<McpCreateNodePlan>.BadRequest(
                $"Missing required fields for type '{selectedType.GlobalId}': {string.Join(", ", missingFields)}.");

        var links = new List<NodePath>();
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in contracts.Values) {
            if (!providedFields.TryGetValue(contract.Name, out var value))
                continue;

            if (contract.ValueKind == NodeFieldValueKind.Node) {
                var nodePlan = await ReadNodeFieldLinks(contract, value).ConfigureAwait(false);
                if (nodePlan.Status != ServiceResultStatus.Ok || nodePlan.Value is null)
                    return ServiceResult<McpCreateNodePlan>.From(nodePlan);
                links.AddRange(nodePlan.Value);
                continue;
            }

            var attribute = ReadPrimitiveFieldAttribute(contract, value);
            if (attribute.Status != ServiceResultStatus.Ok)
                return ServiceResult<McpCreateNodePlan>.From(attribute);
            attributes[contract.Name] = attribute.Value ?? string.Empty;
        }

        return ServiceResult<McpCreateNodePlan>.Ok(new McpCreateNodePlan(attributes, links));
    }

    private static string FormatRequiredField(McpFieldContract contract) {
        if (contract.ValueKind == NodeFieldValueKind.Node) {
            var typeSuffix = contract.AllowedTypes.Count == 0
                ? string.Empty
                : $": {FormatTypeList(contract.AllowedTypes)}";
            return $"{contract.Name} (Node{typeSuffix})";
        }

        return $"{contract.Name} (Primitive: {contract.ClrType.Name})";
    }

    private async Task<ServiceResult<IReadOnlyCollection<NodeFieldDefinition>>> ReadTypeFields(JsonElement? fields) {
        var elements = ReadNamedDefinitionElements(fields, "Fields");
        if (elements.Status != ServiceResultStatus.Ok || elements.Value is null)
            return ServiceResult<IReadOnlyCollection<NodeFieldDefinition>>.From(elements);

        var result = new List<NodeFieldDefinition>();
        foreach (var item in elements.Value) {
            var field = await ReadTypeField(item.Name, item.Value).ConfigureAwait(false);
            if (field.Status != ServiceResultStatus.Ok || field.Value is null)
                return ServiceResult<IReadOnlyCollection<NodeFieldDefinition>>.From(field);
            result.Add(field.Value);
        }

        return ServiceResult<IReadOnlyCollection<NodeFieldDefinition>>.Ok(result);
    }

    private async Task<ServiceResult<NodeFieldDefinition>> ReadTypeField(string name, JsonElement value) {
        if (value.ValueKind != JsonValueKind.Object)
            return ServiceResult<NodeFieldDefinition>.BadRequest($"Field '{name}' definition must be a JSON object.");

        var kindResult = ReadNodeFieldValueKind(value);
        if (kindResult.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeFieldDefinition>.From(kindResult);
        var kind = kindResult.Value;

        var cardinality = ReadTypeCardinality(value, defaultValue: NodeSlotCardinality.Required());
        if (cardinality.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeFieldDefinition>.From(cardinality);
        var maxCardinality = cardinality.Value.Max;
        var inferredIsCollection = !maxCardinality.HasValue || maxCardinality.Value > 1;
        var isCollection = ReadOptionalBool(value, "isCollection")
            ?? inferredIsCollection;

        if (kind == NodeFieldValueKind.Node) {
            var nodeType = await ReadOptionalTypeReference(value, "nodeType").ConfigureAwait(false);
            if (nodeType.Status != ServiceResultStatus.Ok)
                return ServiceResult<NodeFieldDefinition>.From(nodeType);

            var clrType = ReadOptionalClrType(value, defaultType: typeof(Node));
            if (clrType.Status != ServiceResultStatus.Ok || clrType.Value is null)
                return ServiceResult<NodeFieldDefinition>.From(clrType);
            if (!typeof(Node).IsAssignableFrom(clrType.Value))
                return ServiceResult<NodeFieldDefinition>.BadRequest($"Node field '{name}' clrType must be Node or NodeType.");

            return ServiceResult<NodeFieldDefinition>.Ok(new NodeFieldDefinition(
                name,
                NodeFieldValueKind.Node,
                clrType.Value,
                cardinality.Value,
                isCollection,
                nodeType.Value));
        }

        var primitiveType = ReadOptionalClrType(value, defaultType: null);
        if (primitiveType.Status != ServiceResultStatus.Ok || primitiveType.Value is null)
            return ServiceResult<NodeFieldDefinition>.BadRequest($"Primitive field '{name}' must specify clrType.");
        if (typeof(Node).IsAssignableFrom(primitiveType.Value))
            return ServiceResult<NodeFieldDefinition>.BadRequest($"Primitive field '{name}' cannot use node clrType.");

        return ServiceResult<NodeFieldDefinition>.Ok(new NodeFieldDefinition(
            name,
            NodeFieldValueKind.Primitive,
            primitiveType.Value,
            cardinality.Value,
            isCollection));
    }

    private async Task<ServiceResult<IReadOnlyCollection<NodeSlotDefinition>>> ReadTypeSlots(JsonElement? slots) {
        var elements = ReadNamedDefinitionElements(slots, "Slots");
        if (elements.Status != ServiceResultStatus.Ok || elements.Value is null)
            return ServiceResult<IReadOnlyCollection<NodeSlotDefinition>>.From(elements);

        var result = new List<NodeSlotDefinition>();
        foreach (var item in elements.Value) {
            var slot = await ReadTypeSlot(item.Name, item.Value).ConfigureAwait(false);
            if (slot.Status != ServiceResultStatus.Ok || slot.Value is null)
                return ServiceResult<IReadOnlyCollection<NodeSlotDefinition>>.From(slot);
            result.Add(slot.Value);
        }

        return ServiceResult<IReadOnlyCollection<NodeSlotDefinition>>.Ok(result);
    }

    private async Task<ServiceResult<NodeSlotDefinition>> ReadTypeSlot(string name, JsonElement value) {
        if (value.ValueKind != JsonValueKind.Object)
            return ServiceResult<NodeSlotDefinition>.BadRequest($"Slot '{name}' definition must be a JSON object.");

        var cardinality = ReadTypeCardinality(value, defaultValue: NodeSlotCardinality.Required());
        if (cardinality.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeSlotDefinition>.From(cardinality);

        var allowedTypes = await ReadRequiredTypeReferences(value, "allowedTypes", "allowedType").ConfigureAwait(false);
        if (allowedTypes.Status != ServiceResultStatus.Ok || allowedTypes.Value is null)
            return ServiceResult<NodeSlotDefinition>.From(allowedTypes);

        return ServiceResult<NodeSlotDefinition>.Ok(new NodeSlotDefinition(
            name,
            allowedTypes.Value,
            cardinality.Value));
    }

    private async Task<ServiceResult<NodeType?>> ReadOptionalTypeReference(JsonElement value, string propertyName) {
        if (!TryGetProperty(value, propertyName, out var pathValue))
            return ServiceResult<NodeType?>.Ok(null);

        var path = ReadNodePathValue(pathValue);
        if (path.Status != ServiceResultStatus.Ok || path.Value is null)
            return ServiceResult<NodeType?>.From(path);

        var type = await graph.GetTypeNode(path.Value).ConfigureAwait(false);
        return type is null
            ? ServiceResult<NodeType?>.NotFound($"Type node '{path.Value}' was not found or is not a node type.")
            : ServiceResult<NodeType?>.Ok(type);
    }

    private async Task<ServiceResult<IReadOnlyCollection<NodeType>>> ReadRequiredTypeReferences(
        JsonElement value,
        string collectionPropertyName,
        string singlePropertyName) {
        var result = new List<NodeType>();
        if (TryGetProperty(value, collectionPropertyName, out var collectionValue)) {
            if (collectionValue.ValueKind != JsonValueKind.Array)
                return ServiceResult<IReadOnlyCollection<NodeType>>.BadRequest($"'{collectionPropertyName}' must be an array.");
            foreach (var item in collectionValue.EnumerateArray()) {
                var path = ReadNodePathValue(item);
                if (path.Status != ServiceResultStatus.Ok || path.Value is null)
                    return ServiceResult<IReadOnlyCollection<NodeType>>.From(path);
                var type = await graph.GetTypeNode(path.Value).ConfigureAwait(false);
                if (type is null)
                    return ServiceResult<IReadOnlyCollection<NodeType>>.NotFound($"Type node '{path.Value}' was not found or is not a node type.");
                result.Add(type);
            }
        } else if (TryGetProperty(value, singlePropertyName, out var singleValue)) {
            var path = ReadNodePathValue(singleValue);
            if (path.Status != ServiceResultStatus.Ok || path.Value is null)
                return ServiceResult<IReadOnlyCollection<NodeType>>.From(path);
            var type = await graph.GetTypeNode(path.Value).ConfigureAwait(false);
            if (type is null)
                return ServiceResult<IReadOnlyCollection<NodeType>>.NotFound($"Type node '{path.Value}' was not found or is not a node type.");
            result.Add(type);
        }

        return result.Count == 0
            ? ServiceResult<IReadOnlyCollection<NodeType>>.BadRequest($"Slot must specify '{collectionPropertyName}' or '{singlePropertyName}'.")
            : ServiceResult<IReadOnlyCollection<NodeType>>.Ok(result);
    }

    private static ServiceResult<IReadOnlyCollection<McpNamedJsonElement>> ReadNamedDefinitionElements(
        JsonElement? value,
        string subject) {
        if (!HasJsonValue(value))
            return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.Ok(Array.Empty<McpNamedJsonElement>());

        var root = value!.Value;
        if (root.ValueKind == JsonValueKind.String) {
            var text = root.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.Ok(Array.Empty<McpNamedJsonElement>());
            try {
                using var document = JsonDocument.Parse(text);
                return ReadNamedDefinitionElements(document.RootElement.Clone(), subject);
            } catch (JsonException ex) {
                return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} must be a JSON object or array: {ex.Message}");
            }
        }

        if (root.ValueKind == JsonValueKind.Object) {
            var result = new List<McpNamedJsonElement>();
            foreach (var property in root.EnumerateObject()) {
                if (string.IsNullOrWhiteSpace(property.Name))
                    return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} name cannot be empty.");
                result.Add(new McpNamedJsonElement(property.Name.Trim(), property.Value.Clone()));
            }
            return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.Ok(result);
        }

        if (root.ValueKind == JsonValueKind.Array) {
            var result = new List<McpNamedJsonElement>();
            foreach (var item in root.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.Object)
                    return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} array items must be JSON objects.");
                if (!TryGetProperty(item, "name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String)
                    return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} array items must contain a string 'name'.");
                var name = nameValue.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} name cannot be empty.");
                result.Add(new McpNamedJsonElement(name.Trim(), item.Clone()));
            }
            return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.Ok(result);
        }

        return ServiceResult<IReadOnlyCollection<McpNamedJsonElement>>.BadRequest($"{subject} must be a JSON object or array.");
    }

    private static ServiceResult<NodeFieldValueKind> ReadNodeFieldValueKind(JsonElement value) {
        if (TryGetProperty(value, "valueKind", out var valueKind) || TryGetProperty(value, "kind", out valueKind)) {
            if (valueKind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<NodeFieldValueKind>(valueKind.GetString(), ignoreCase: true, out var parsed))
                return ServiceResult<NodeFieldValueKind>.BadRequest("Field valueKind must be 'Node' or 'Primitive'.");
            return ServiceResult<NodeFieldValueKind>.Ok(parsed);
        }

        if (TryGetProperty(value, "nodeType", out _))
            return ServiceResult<NodeFieldValueKind>.Ok(NodeFieldValueKind.Node);
        if (TryGetProperty(value, "clrType", out _))
            return ServiceResult<NodeFieldValueKind>.Ok(NodeFieldValueKind.Primitive);

        return ServiceResult<NodeFieldValueKind>.BadRequest("Field must specify valueKind, nodeType, or clrType.");
    }

    private static ServiceResult<NodeSlotCardinality> ReadTypeCardinality(
        JsonElement value,
        NodeSlotCardinality defaultValue) {
        if (TryGetProperty(value, "cardinality", out var cardinality))
            return ReadCardinalityValue(cardinality);

        var hasMin = TryGetProperty(value, "min", out var minValue);
        var hasMax = TryGetProperty(value, "max", out var maxValue);
        if (!hasMin && !hasMax)
            return ServiceResult<NodeSlotCardinality>.Ok(defaultValue);

        var min = hasMin ? ReadNonNegativeInt(minValue, "min") : ServiceResult<int>.Ok(defaultValue.Min);
        if (min.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeSlotCardinality>.From(min);
        var max = hasMax ? ReadOptionalMax(maxValue) : ServiceResult<int?>.Ok(defaultValue.Max);
        if (max.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeSlotCardinality>.From(max);

        return CreateCardinality(min.Value, max.Value);
    }

    private static ServiceResult<NodeSlotCardinality> ReadCardinalityValue(JsonElement value) {
        if (value.ValueKind == JsonValueKind.String) {
            var text = value.GetString()?.Trim();
            if (string.Equals(text, "required", StringComparison.OrdinalIgnoreCase))
                return ServiceResult<NodeSlotCardinality>.Ok(NodeSlotCardinality.Required());
            if (string.Equals(text, "optional", StringComparison.OrdinalIgnoreCase))
                return ServiceResult<NodeSlotCardinality>.Ok(NodeSlotCardinality.Optional());
            if (string.Equals(text, "many", StringComparison.OrdinalIgnoreCase))
                return ServiceResult<NodeSlotCardinality>.Ok(NodeSlotCardinality.Many());
            if (string.Equals(text, "oneOrMore", StringComparison.OrdinalIgnoreCase))
                return ServiceResult<NodeSlotCardinality>.Ok(NodeSlotCardinality.Many(1));
            if (TryParseCardinalityRange(text, out var range))
                return ServiceResult<NodeSlotCardinality>.Ok(range);
            return ServiceResult<NodeSlotCardinality>.BadRequest("Cardinality string must be required, optional, many, oneOrMore, or a range like 1..1.");
        }

        if (value.ValueKind != JsonValueKind.Object)
            return ServiceResult<NodeSlotCardinality>.BadRequest("Cardinality must be a string or object.");

        var min = TryGetProperty(value, "min", out var minValue)
            ? ReadNonNegativeInt(minValue, "min")
            : ServiceResult<int>.Ok(0);
        if (min.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeSlotCardinality>.From(min);
        var max = TryGetProperty(value, "max", out var maxValue)
            ? ReadOptionalMax(maxValue)
            : ServiceResult<int?>.Ok(null);
        if (max.Status != ServiceResultStatus.Ok)
            return ServiceResult<NodeSlotCardinality>.From(max);

        return CreateCardinality(min.Value, max.Value);
    }

    private static ServiceResult<NodeSlotCardinality> CreateCardinality(int min, int? max) {
        if (max is { } value && value < min)
            return ServiceResult<NodeSlotCardinality>.BadRequest("Cardinality max cannot be lower than min.");
        return ServiceResult<NodeSlotCardinality>.Ok(new NodeSlotCardinality(min, max));
    }

    private static bool TryParseCardinalityRange(string? value, out NodeSlotCardinality cardinality) {
        cardinality = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split("..", StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min) || min < 0)
            return false;
        if (parts[1] == "*") {
            cardinality = new NodeSlotCardinality(min, null);
            return true;
        }
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < min)
            return false;
        cardinality = new NodeSlotCardinality(min, max);
        return true;
    }

    private static ServiceResult<int> ReadNonNegativeInt(JsonElement value, string propertyName) {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 0)
            return ServiceResult<int>.BadRequest($"'{propertyName}' must be a non-negative integer.");
        return ServiceResult<int>.Ok(result);
    }

    private static ServiceResult<int?> ReadOptionalMax(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Null)
            return ServiceResult<int?>.Ok(null);
        if (value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() == "*")
            return ServiceResult<int?>.Ok(null);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 0)
            return ServiceResult<int?>.BadRequest("'max' must be a non-negative integer, null, or '*'.");
        return ServiceResult<int?>.Ok(result);
    }

    private static ServiceResult<Type?> ReadOptionalClrType(JsonElement value, Type? defaultType) {
        if (!TryGetProperty(value, "clrType", out var clrType))
            return ServiceResult<Type?>.Ok(defaultType);
        if (clrType.ValueKind != JsonValueKind.String)
            return ServiceResult<Type?>.BadRequest("clrType must be a string.");
        return ResolveClrType(clrType.GetString());
    }

    private static ServiceResult<Type?> ResolveClrType(string? value) {
        if (string.IsNullOrWhiteSpace(value))
            return ServiceResult<Type?>.BadRequest("clrType cannot be empty.");

        var normalized = value.Trim();
        var lower = normalized.ToLowerInvariant();
        var type = lower switch {
            "string" => typeof(string),
            "int" or "integer" or "system.int32" => typeof(int),
            "long" or "system.int64" => typeof(long),
            "short" or "system.int16" => typeof(short),
            "byte" or "system.byte" => typeof(byte),
            "bool" or "boolean" or "system.boolean" => typeof(bool),
            "decimal" or "system.decimal" => typeof(decimal),
            "double" or "system.double" => typeof(double),
            "float" or "single" or "system.single" => typeof(float),
            "datetime" or "system.datetime" => typeof(DateTime),
            "datetimeoffset" or "system.datetimeoffset" => typeof(DateTimeOffset),
            "guid" or "system.guid" => typeof(Guid),
            "node" => typeof(Node),
            "nodetype" => typeof(NodeType),
            _ => Type.GetType(normalized, throwOnError: false, ignoreCase: true)
        };

        return type is null
            ? ServiceResult<Type?>.BadRequest($"CLR type '{value}' cannot be resolved.")
            : ServiceResult<Type?>.Ok(type);
    }

    private static bool? ReadOptionalBool(JsonElement value, string propertyName) {
        if (!TryGetProperty(value, propertyName, out var property))
            return null;
        return property.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private async Task<ServiceResult<IReadOnlyCollection<NodePath>>> ReadNodeFieldLinks(
        McpFieldContract contract,
        JsonElement value) {
        var paths = ReadNodeFieldPaths(contract, value);
        if (paths.Status != ServiceResultStatus.Ok || paths.Value is null)
            return ServiceResult<IReadOnlyCollection<NodePath>>.From(paths);

        if (!contract.Cardinality.Contains(paths.Value.Count))
            return ServiceResult<IReadOnlyCollection<NodePath>>.BadRequest(
                $"Field '{contract.Name}' expects {contract.Cardinality}, but got {paths.Value.Count}.");

        var links = new List<NodePath>();
        foreach (var path in paths.Value) {
            var node = await graph.GetSemanticNodeAsync(path).ConfigureAwait(false);
            if (node.Status != ServiceResultStatus.Ok || node.Value is null)
                return node.Status == ServiceResultStatus.NotFound
                    ? ServiceResult<IReadOnlyCollection<NodePath>>.NotFound(
                        $"Field '{contract.Name}' references node '{path}', but it was not found.")
                    : new ServiceResult<IReadOnlyCollection<NodePath>>(node.Status, Error: node.Error);

            if (contract.AllowedTypes.Count > 0 && !HasAnyType(node.Value, contract.AllowedTypes))
                return ServiceResult<IReadOnlyCollection<NodePath>>.BadRequest(
                    $"Field '{contract.Name}' references node '{node.Value.GlobalId}', but expected one of: {FormatTypeList(contract.AllowedTypes)}.");

            links.Add((NodePath)node.Value.GlobalId);
        }

        return ServiceResult<IReadOnlyCollection<NodePath>>.Ok(links);
    }

    private static ServiceResult<IReadOnlyCollection<NodePath>> ReadNodeFieldPaths(
        McpFieldContract contract,
        JsonElement value) {
        var paths = new List<NodePath>();
        if (contract.IsCollection) {
            if (value.ValueKind == JsonValueKind.Array) {
                foreach (var item in value.EnumerateArray()) {
                    var itemPath = ReadNodePathValue(item);
                    if (itemPath.Status != ServiceResultStatus.Ok || itemPath.Value is null)
                        return ServiceResult<IReadOnlyCollection<NodePath>>.From(itemPath);
                    paths.Add(itemPath.Value);
                }
            } else {
                var itemPath = ReadNodePathValue(value);
                if (itemPath.Status != ServiceResultStatus.Ok || itemPath.Value is null)
                    return ServiceResult<IReadOnlyCollection<NodePath>>.From(itemPath);
                paths.Add(itemPath.Value);
            }
        } else {
            var path = ReadNodePathValue(value);
            if (path.Status != ServiceResultStatus.Ok || path.Value is null)
                return ServiceResult<IReadOnlyCollection<NodePath>>.From(path);
            paths.Add(path.Value);
        }

        return ServiceResult<IReadOnlyCollection<NodePath>>.Ok(paths);
    }

    private static ServiceResult<string> ReadPrimitiveFieldAttribute(McpFieldContract contract, JsonElement value) {
        if (contract.IsCollection) {
            if (value.ValueKind != JsonValueKind.Array) {
                if (!contract.Cardinality.Contains(1))
                    return ServiceResult<string>.BadRequest(
                        $"Field '{contract.Name}' expects {contract.Cardinality}, but got 1.");
                return ReadSinglePrimitiveFieldAttribute(contract, value);
            }

            var count = 0;
            foreach (var item in value.EnumerateArray()) {
                var validate = ReadSinglePrimitiveFieldAttribute(contract, item);
                if (validate.Status != ServiceResultStatus.Ok)
                    return validate;
                count++;
            }

            if (!contract.Cardinality.Contains(count))
                return ServiceResult<string>.BadRequest(
                    $"Field '{contract.Name}' expects {contract.Cardinality}, but got {count}.");
            return ServiceResult<string>.Ok(value.GetRawText());
        }

        return ReadSinglePrimitiveFieldAttribute(contract, value);
    }

    private static ServiceResult<string> ReadSinglePrimitiveFieldAttribute(McpFieldContract contract, JsonElement value) {
        var targetType = Nullable.GetUnderlyingType(contract.ClrType) ?? contract.ClrType;
        try {
            var parsed = JsonSerializer.Deserialize(value, targetType, JsonOptions);
            if (parsed is null)
                return ServiceResult<string>.BadRequest($"Field '{contract.Name}' cannot be null.");

            var text = parsed switch {
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => parsed.ToString()
            };
            return ServiceResult<string>.Ok(text ?? string.Empty);
        } catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException) {
            return ServiceResult<string>.BadRequest(
                $"Field '{contract.Name}' must be a JSON value compatible with '{FormatClrType(targetType)}'.");
        }
    }

    private static ServiceResult<Dictionary<string, JsonElement>> ReadFieldsObject(JsonElement? fields) {
        if (!HasJsonValue(fields))
            return ServiceResult<Dictionary<string, JsonElement>>.Ok(new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));

        var value = fields!.Value;
        if (value.ValueKind == JsonValueKind.String) {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return ServiceResult<Dictionary<string, JsonElement>>.Ok(new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));

            try {
                using var document = JsonDocument.Parse(text);
                return ReadFieldsObject(document.RootElement.Clone());
            } catch (JsonException ex) {
                return ServiceResult<Dictionary<string, JsonElement>>.BadRequest($"Fields must be a JSON object: {ex.Message}");
            }
        }

        if (value.ValueKind != JsonValueKind.Object)
            return ServiceResult<Dictionary<string, JsonElement>>.BadRequest("Fields must be a JSON object.");

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject()) {
            if (string.IsNullOrWhiteSpace(property.Name))
                return ServiceResult<Dictionary<string, JsonElement>>.BadRequest("Field name cannot be empty.");
            result[property.Name] = property.Value.Clone();
        }

        return ServiceResult<Dictionary<string, JsonElement>>.Ok(result);
    }

    private static IReadOnlyDictionary<string, McpFieldContract> BuildFieldContracts(NodeTypeDefinition definition) {
        var contracts = new Dictionary<string, McpFieldContract>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields) {
            contracts[field.Name] = new McpFieldContract(
                field.Name,
                field.ValueKind,
                field.ClrType,
                field.Cardinality,
                field.IsCollection,
                field.NodeType is null ? Array.Empty<NodeType>() : [field.NodeType]);
        }

        foreach (var slot in definition.Slots) {
            if (contracts.TryGetValue(slot.Name, out var existing)) {
                contracts[slot.Name] = existing with {
                    ValueKind = NodeFieldValueKind.Node,
                    Cardinality = slot.Cardinality,
                    AllowedTypes = slot.AllowedTypes
                };
                continue;
            }

            contracts[slot.Name] = new McpFieldContract(
                slot.Name,
                NodeFieldValueKind.Node,
                typeof(Node),
                slot.Cardinality,
                slot.Cardinality.Max != 1,
                slot.AllowedTypes);
        }

        return contracts;
    }

    private static ServiceResult<NodePath> ReadNodePathValue(JsonElement value) {
        switch (value.ValueKind) {
            case JsonValueKind.String:
                var path = ParseNodePath(value.GetString());
                return path is null
                    ? ServiceResult<NodePath>.BadRequest("Node path cannot be empty.")
                    : ServiceResult<NodePath>.Ok(path);
            case JsonValueKind.Array:
                var segments = new List<string>();
                foreach (var item in value.EnumerateArray()) {
                    if (item.ValueKind != JsonValueKind.String)
                        return ServiceResult<NodePath>.BadRequest("Node path arrays must contain only string segments.");
                    var segment = item.GetString();
                    if (string.IsNullOrWhiteSpace(segment))
                        return ServiceResult<NodePath>.BadRequest("Node path segments cannot be empty.");
                    segments.Add(segment.Trim());
                }
                return segments.Count == 0
                    ? ServiceResult<NodePath>.BadRequest("Node path cannot be empty.")
                    : ServiceResult<NodePath>.Ok(new NodePath(segments));
            case JsonValueKind.Object:
                if (value.TryGetProperty("path", out var pathValue))
                    return ReadNodePathValue(pathValue);
                return ServiceResult<NodePath>.BadRequest("Node field objects must contain a 'path' property.");
            default:
                return ServiceResult<NodePath>.BadRequest("Node fields must be paths encoded as string, string array, or object with a path property.");
        }
    }

    private static NodePath? ParseNodePath(string? value) {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var segments = value
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        return segments.Length == 0 ? null : new NodePath(segments);
    }

    private static bool HasAnyType(InstanceNode node, IReadOnlyCollection<NodeType> allowedTypes) {
        var allowedTypeIds = allowedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        return node.AssignedTypes.Any(type => allowedTypeIds.Contains(type.GlobalId));
    }

    private static bool HasJsonValue(JsonElement? value) =>
        value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null };

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement property) {
        if (value.ValueKind == JsonValueKind.Object) {
            foreach (var candidate in value.EnumerateObject()) {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)) {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string FormatClrType(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type).FullName ?? type.Name;

    private static string FormatTypeList(IEnumerable<NodeType> types) =>
        string.Join(", ", types.Select(static type => type.GlobalId.ToString()));

    private static McpCardinalityResponse ToCardinalityResponse(NodeSlotCardinality cardinality) {
        return new McpCardinalityResponse {
            Min = cardinality.Min,
            Max = cardinality.Max,
            Text = cardinality.ToString()
        };
    }

    private static McpSubgraphResponse ToSubgraphResponse(Subgraph subgraph) {
        var nodes = subgraph.Nodes.ToArray();
        var nodeIds = nodes
            .Select(static node => node.GlobalId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = nodes
            .SelectMany(static node => node.Edges)
            .Where(edge => nodeIds.Contains(edge.Node1.GlobalId.ToString()) && nodeIds.Contains(edge.Node2.GlobalId.ToString()))
            .GroupBy(static edge => EdgeKey(edge.Node1.GlobalId.ToString(), edge.Node2.GlobalId.ToString()), StringComparer.OrdinalIgnoreCase)
            .Select(static group => ToEdgeResponse(group.First()))
            .ToArray();

        return new McpSubgraphResponse {
            Nodes = nodes.Select(static node => ToNodeResponse(node)).ToArray(),
            Edges = edges
        };
    }

    private static McpNodeResponse ToNodeResponse(Node node) {
        return new McpNodeResponse {
            LocalId = node.LocalId.ToString(),
            InternalId = node.GlobalId.ToString(),
            Edges = node.Edges.Select(edge => ToNodeEdgeResponse(node, edge)).ToArray()
        };
    }

    private static McpSemanticNodeResponse ToSemanticNodeResponse(InstanceNode node) {
        return new McpSemanticNodeResponse {
            LocalId = node.LocalId.ToString(),
            InternalId = node.GlobalId.ToString(),
            Types = node.TypeInstances
                .OrderBy(static instance => instance.Type.GlobalId.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(static instance => new McpSemanticNodeTypeResponse {
                    TypeInternalId = instance.Type.GlobalId.ToString(),
                    WitnessInternalId = instance.Witness?.GlobalId.ToString(),
                    IsMaterialized = instance.IsMaterialized
                })
                .ToArray()
        };
    }

    private static McpNodeSearchMatchResponse ToSearchMatchResponse(NodeSearchMatch match) {
        return new McpNodeSearchMatchResponse {
            Node = ToNodeResponse(match.Node),
            Bindings = match.Bindings.ToDictionary(
                static binding => binding.Key,
                static binding => ToNodeResponse(binding.Value),
                StringComparer.OrdinalIgnoreCase),
            Score = match.Score,
            MatchedBy = match.MatchedBy
        };
    }

    private static McpEdgeResponse ToEdgeResponse(Node source, Node target) {
        return string.Compare(source.GlobalId.ToString(), target.GlobalId.ToString(), StringComparison.OrdinalIgnoreCase) <= 0
            ? ToOrderedEdgeResponse(source, target)
            : ToOrderedEdgeResponse(target, source);
    }

    private static McpEdgeResponse ToEdgeResponse(Edge edge) {
        return ToEdgeResponse(edge.Node1, edge.Node2);
    }

    private static McpEdgeResponse ToNodeEdgeResponse(Node node, Edge edge) {
        var neighbor = edge.Node1.GlobalId == node.GlobalId
            ? edge.Node2
            : edge.Node1;

        return ToOrderedEdgeResponse(edge.Node1, edge.Node2, neighbor.LocalId.ToString());
    }

    private static McpEdgeResponse ToOrderedEdgeResponse(Node source, Node target, string? neighborLocalId = null) {
        if (string.Compare(source.GlobalId.ToString(), target.GlobalId.ToString(), StringComparison.OrdinalIgnoreCase) > 0)
            (source, target) = (target, source);

        return new McpEdgeResponse {
            NeighborLocalId = neighborLocalId,
            Node1LocalId = source.LocalId.ToString(),
            Node1InternalId = source.GlobalId.ToString(),
            Node2LocalId = target.LocalId.ToString(),
            Node2InternalId = target.GlobalId.ToString()
        };
    }

    private static string EdgeKey(string node1InternalId, string node2InternalId) {
        return string.Compare(node1InternalId, node2InternalId, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{node1InternalId}\0{node2InternalId}"
            : $"{node2InternalId}\0{node1InternalId}";
    }

    private static bool ContainsMcpHiddenSearchPredicate(JsonElement value) {
        switch (value.ValueKind) {
            case JsonValueKind.Object:
                if (value.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && string.Equals(kind.GetString(), "attribute", StringComparison.OrdinalIgnoreCase))
                    return true;

                foreach (var property in value.EnumerateObject())
                    if (ContainsMcpHiddenSearchPredicate(property.Value))
                        return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    if (ContainsMcpHiddenSearchPredicate(item))
                        return true;
                return false;
            default:
                return false;
        }
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
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.AllowOutOfOrderMetadataProperties = true;
        options.WriteIndented = true;
        return options;
    }

    private sealed record McpCreateNodePlan(
        IReadOnlyDictionary<string, string> PrimitiveAttributes,
        IReadOnlyCollection<NodePath> NodeLinks);

    private sealed record McpFieldContract(
        string Name,
        NodeFieldValueKind ValueKind,
        Type ClrType,
        NodeSlotCardinality Cardinality,
        bool IsCollection,
        IReadOnlyCollection<NodeType> AllowedTypes);

    private sealed record McpNamedJsonElement(
        string Name,
        JsonElement Value);

    private sealed record McpNodeTypeDefinitionResponse {
        public required string LocalId { get; init; }

        public required string InternalId { get; init; }

        public bool IsAbstract { get; init; }

        public IReadOnlyCollection<string> RequiredTypeInternalIds { get; init; } =
            Array.Empty<string>();

        public IReadOnlyCollection<McpNodeTypeFieldResponse> Fields { get; init; } =
            Array.Empty<McpNodeTypeFieldResponse>();

        public IReadOnlyCollection<McpNodeTypeSlotResponse> Slots { get; init; } =
            Array.Empty<McpNodeTypeSlotResponse>();
    }

    private sealed record McpNodeTypeFieldResponse {
        public required string Name { get; init; }

        public required string ValueKind { get; init; }

        public required string ClrType { get; init; }

        public required McpCardinalityResponse Cardinality { get; init; }

        public bool IsCollection { get; init; }

        public string? NodeTypeInternalId { get; init; }
    }

    private sealed record McpNodeTypeSlotResponse {
        public required string Name { get; init; }

        public required McpCardinalityResponse Cardinality { get; init; }

        public IReadOnlyCollection<string> AllowedTypeInternalIds { get; init; } =
            Array.Empty<string>();
    }

    private sealed record McpCardinalityResponse {
        public int Min { get; init; }

        public int? Max { get; init; }

        public required string Text { get; init; }
    }

    private sealed record McpNodeResponse {
        public required string LocalId { get; init; }

        public required string InternalId { get; init; }

        public IReadOnlyCollection<McpEdgeResponse> Edges { get; init; } = Array.Empty<McpEdgeResponse>();
    }

    private sealed record McpSemanticNodeResponse {
        public required string LocalId { get; init; }

        public required string InternalId { get; init; }

        public IReadOnlyCollection<McpSemanticNodeTypeResponse> Types { get; init; } =
            Array.Empty<McpSemanticNodeTypeResponse>();
    }

    private sealed record McpSemanticNodeTypeResponse {
        public required string TypeInternalId { get; init; }

        public string? WitnessInternalId { get; init; }

        public bool IsMaterialized { get; init; }
    }

    private sealed record McpEdgeResponse {
        public string? NeighborLocalId { get; init; }

        public required string Node1LocalId { get; init; }

        public required string Node1InternalId { get; init; }

        public required string Node2LocalId { get; init; }

        public required string Node2InternalId { get; init; }
    }

    private sealed record McpSubgraphResponse {
        public IReadOnlyCollection<McpNodeResponse> Nodes { get; init; } = Array.Empty<McpNodeResponse>();

        public IReadOnlyCollection<McpEdgeResponse> Edges { get; init; } = Array.Empty<McpEdgeResponse>();
    }

    private sealed record McpNodeSearchMatchResponse {
        public required McpNodeResponse Node { get; init; }

        public IReadOnlyDictionary<string, McpNodeResponse> Bindings { get; init; } =
            new Dictionary<string, McpNodeResponse>(StringComparer.OrdinalIgnoreCase);

        public double Score { get; init; }

        public IReadOnlyCollection<string> MatchedBy { get; init; } = Array.Empty<string>();
    }

    private sealed class TypedCreateException(ServiceResultStatus status, string message) : Exception(message) {
        public ServiceResultStatus Status { get; } = status;
    }
}
