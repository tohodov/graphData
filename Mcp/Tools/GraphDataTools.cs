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

        var definition = schemaGraph.GetNodeTypeDefinition(typeNode);
        var plan = await BuildCreateNodePlan(definition, fields).ConfigureAwait(false);
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

            var reload = await graph.GetNode(createdPath).ConfigureAwait(false);
            return ToMutationJson(reload, "node", static node => ToNodeResponse(node));
        } catch (TypedCreateException ex) {
            await graph.DeleteNode(createdPath).ConfigureAwait(false);
            return ToJson(ToErrorResponse(ex.Status, $"Typed node creation was rolled back: {ex.Message}"));
        } catch (Exception ex) {
            await graph.DeleteNode(createdPath).ConfigureAwait(false);
            return ToJson(ToErrorResponse(ServiceResultStatus.BadRequest, $"Typed node creation was rolled back: {ex.Message}"));
        }
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
        NodeTypeDefinition definition,
        JsonElement? fields) {
        var fieldsResult = ReadFieldsObject(fields);
        if (fieldsResult.Status != ServiceResultStatus.Ok || fieldsResult.Value is null)
            return ServiceResult<McpCreateNodePlan>.From(fieldsResult);

        var providedFields = fieldsResult.Value;
        var contracts = BuildFieldContracts(definition);
        foreach (var name in providedFields.Keys)
            if (!contracts.ContainsKey(name))
                return ServiceResult<McpCreateNodePlan>.BadRequest(
                    $"Type '{definition.Type.GlobalId}' does not define field '{name}'.");

        var links = new List<NodePath>();
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in contracts.Values) {
            if (!providedFields.TryGetValue(contract.Name, out var value)) {
                if (!contract.Cardinality.Contains(0))
                    return ServiceResult<McpCreateNodePlan>.BadRequest(
                        $"Field '{contract.Name}' is required by type '{definition.Type.GlobalId}'.");
                continue;
            }

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
            var node = await graph.GetNode(path).ConfigureAwait(false);
            if (node.Status != ServiceResultStatus.Ok || node.Value is null)
                return ServiceResult<IReadOnlyCollection<NodePath>>.NotFound(
                    $"Field '{contract.Name}' references node '{path}', but it was not found.");

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

    private static bool HasAnyType(Node node, IReadOnlyCollection<NodeType> allowedTypes) {
        var allowedTypeIds = allowedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        return node.Nodes.Any(neighbor => allowedTypeIds.Contains(neighbor.GlobalId));
    }

    private static bool HasJsonValue(JsonElement? value) =>
        value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null };

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

    private sealed record McpNodeTypeDefinitionResponse {
        public required string LocalId { get; init; }

        public required string InternalId { get; init; }

        public bool IsAbstract { get; init; }

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
