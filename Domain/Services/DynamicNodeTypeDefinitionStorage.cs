using System.Globalization;
using System.Text.Json;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal static class DynamicNodeTypeDefinitionStorage
{
    private static readonly NodeLocalId DefinitionNodeId = new("Definition");
    private static readonly NodeLocalId FieldsNodeId = new("Fields");
    private static readonly NodeLocalId SlotsNodeId = new("Slots");

    private const string IsAbstractAttribute = "isAbstract";
    private const string ElementKindAttribute = "elementKind";
    private const string ValueKindAttribute = "valueKind";
    private const string ClrTypeAttribute = "clrType";
    private const string MinAttribute = "min";
    private const string MaxAttribute = "max";
    private const string IsCollectionAttribute = "isCollection";
    private const string NodeTypePathAttribute = "nodeTypePath";
    private const string AllowedTypePathsAttribute = "allowedTypePaths";
    private const string MaterializationCompleteAttribute = "materializationComplete";

    public static async Task WriteAsync(ISemanticGraphStorage storage, NodeTypeDefinition definition)
    {
        var definitionAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            [IsAbstractAttribute] = definition.IsAbstract.ToString(CultureInfo.InvariantCulture),
            [ElementKindAttribute] = definition.ElementKind.ToString(),
            [MaterializationCompleteAttribute] = bool.FalseString
        };
        var definitionNode = await UpsertInfrastructureAsync(
            storage,
            DefinitionNodeId,
            definition.Type.GlobalId,
            definitionAttributes).ConfigureAwait(false);

        var fieldsNode = await UpsertInfrastructureAsync(
            storage,
            FieldsNodeId,
            definitionNode.GlobalId).ConfigureAwait(false);
        foreach (var field in definition.Fields) {
            _ = await UpsertInfrastructureAsync(
                storage,
                new NodeLocalId(field.Name),
                fieldsNode.GlobalId,
                CreateFieldAttributes(field)).ConfigureAwait(false);
        }

        var slotsNode = await UpsertInfrastructureAsync(
            storage,
            SlotsNodeId,
            definitionNode.GlobalId).ConfigureAwait(false);
        foreach (var slot in definition.Slots.Where(slot => !IsDerivedFromField(slot, definition.Fields))) {
            _ = await UpsertInfrastructureAsync(
                storage,
                new NodeLocalId(slot.Name),
                slotsNode.GlobalId,
                CreateCardinalityAttributes(slot.Cardinality, slot.AllowedTypes)).ConfigureAwait(false);
        }

        definitionAttributes[MaterializationCompleteAttribute] = bool.TrueString;
        await storage.ReplaceTypeElementAsync(
            definitionNode.GlobalId,
            SemanticElementKind.TypeInfrastructure,
            Array.Empty<NodeRef>(),
            definitionAttributes).ConfigureAwait(false);
    }

    public static NodeTypeDefinition? TryRead(NodeType typeNode)
    {
        var definitionNode = FindNeighbor(typeNode, DefinitionNodeId);
        if (definitionNode is null)
            return null;
        if (!definitionNode.Attributes.TryGetValue(MaterializationCompleteAttribute, out var complete)
            || !bool.TryParse(complete, out var isComplete)
            || !isComplete)
            return null;

        var nodeTypesRoot = FindNodeTypesRoot(typeNode);
        var fieldsNode = FindNeighbor(definitionNode, FieldsNodeId);
        var slotsNode = FindNeighbor(definitionNode, SlotsNodeId);
        if (fieldsNode is null || slotsNode is null)
            return null;
        var fields = ReadFields(fieldsNode, nodeTypesRoot).ToArray();
        var explicitSlots = ReadSlots(slotsNode, nodeTypesRoot).ToArray();
        var slots = explicitSlots
            .Concat(fields.Select(static field => field.ToSlotDefinition()).OfType<NodeSlotDefinition>())
            .DistinctBy(SlotIdentity)
            .ToArray();

        return new NodeTypeDefinition(
            typeNode,
            ReadBool(definitionNode.Attributes, IsAbstractAttribute),
            slots,
            fields) {
            ElementKind = ReadElementKind(definitionNode.Attributes)
        };
    }

    private static IEnumerable<NodeFieldDefinition> ReadFields(Node fieldsNode, Node? nodeTypesRoot)
    {
        foreach (var fieldNode in fieldsNode.Nodes.Where(node => node.LocalId != DefinitionNodeId)) {
            var attributes = fieldNode.Attributes;
            var valueKind = ReadValueKind(attributes);
            var clrType = ReadClrType(attributes);
            var cardinality = ReadCardinality(attributes);
            var isCollection = ReadBool(attributes, IsCollectionAttribute);
            var allowedTypes = ReadStoredNodeTypes(
                attributes,
                NodeTypePathAttribute,
                nodeTypesRoot,
                () => ReadLinkedNodeTypes(fieldNode, nodeTypesRoot, fieldsNode.GlobalId)).ToArray();
            var nodeType = allowedTypes.Length switch {
                0 => null,
                1 => allowedTypes[0],
                _ => throw new InvalidOperationException(
                    $"Dynamic node field '{fieldNode.GlobalId}' can reference at most one node type.")
            };

            yield return new NodeFieldDefinition(
                fieldNode.LocalId.ToString(),
                valueKind,
                clrType,
                cardinality,
                isCollection,
                nodeType);
        }
    }

    private static IEnumerable<NodeSlotDefinition> ReadSlots(Node slotsNode, Node? nodeTypesRoot)
    {
        foreach (var slotNode in slotsNode.Nodes.Where(node => node.LocalId != DefinitionNodeId)) {
            var allowedTypes = ReadStoredNodeTypes(
                slotNode.Attributes,
                AllowedTypePathsAttribute,
                nodeTypesRoot,
                () => ReadLinkedNodeTypes(slotNode, nodeTypesRoot, slotsNode.GlobalId)).ToArray();
            yield return new NodeSlotDefinition(
                slotNode.LocalId.ToString(),
                allowedTypes,
                ReadCardinality(slotNode.Attributes));
        }
    }

    private static IEnumerable<NodeType> ReadLinkedNodeTypes(Node owner, Node? nodeTypesRoot, InternalId containerId)
    {
        if (nodeTypesRoot is null)
            yield break;

        foreach (var neighbor in owner.Nodes) {
            if (neighbor.GlobalId == containerId)
                continue;
            if (neighbor.GlobalId == nodeTypesRoot.GlobalId)
                continue;
            if (IsNodeType(neighbor, nodeTypesRoot))
                yield return new NodeType(neighbor.Backing);
        }
    }

    private static Node? FindNodeTypesRoot(NodeType typeNode)
    {
        Node current = typeNode;
        while (current.GlobalId.Count() > 1) {
            var parentId = new InternalId(current.GlobalId.Take(current.GlobalId.Count() - 1));
            current = current.Nodes.SingleOrDefault(node => node.GlobalId == parentId)
                ?? throw new InvalidOperationException(
                    $"Type catalog parent '{parentId}' of '{current.GlobalId}' is unavailable.");
        }

        return current.GlobalId.ToString() == "NodeTypes"
            ? current
            : null;
    }

    private static bool IsNodeType(Node node, Node nodeTypesRoot)
    {
        return node.GlobalId != nodeTypesRoot.GlobalId
            && node.Backing is ISemanticNodeBacking { SemanticKind: SemanticElementKind.Type };
    }

    private static Node? FindNeighbor(Node owner, NodeLocalId localId)
    {
        var expectedId = new InternalId(owner.GlobalId.Append(localId));
        return owner.Nodes.SingleOrDefault(node => node.GlobalId == expectedId);
    }

    private static async Task<NodeBacking> UpsertInfrastructureAsync(
        ISemanticGraphStorage storage,
        NodeLocalId localId,
        InternalId parentId,
        IDictionary<string, string>? attributes = null)
    {
        var id = new InternalId(parentId.Append(localId));
        var existing = await storage.ReadSemanticElementAsync(id).ConfigureAwait(false);
        return existing is null
            ? await storage.CreateTypeElementAsync(
                localId,
                parentId,
                SemanticElementKind.TypeInfrastructure,
                Array.Empty<NodeRef>(),
                attributes).ConfigureAwait(false)
            : await storage.ReplaceTypeElementAsync(
                id,
                SemanticElementKind.TypeInfrastructure,
                Array.Empty<NodeRef>(),
                attributes).ConfigureAwait(false);
    }

    private static Dictionary<string, string> CreateFieldAttributes(NodeFieldDefinition field)
    {
        var attributes = CreateCardinalityAttributes(field.Cardinality);
        attributes[ValueKindAttribute] = field.ValueKind.ToString();
        attributes[ClrTypeAttribute] = SerializeClrType(field.ClrType);
        attributes[IsCollectionAttribute] = field.IsCollection.ToString(CultureInfo.InvariantCulture);
        if (field.NodeType is not null)
            attributes[NodeTypePathAttribute] = field.NodeType.GlobalId.ToString();
        return attributes;
    }

    private static Dictionary<string, string> CreateCardinalityAttributes(NodeSlotCardinality cardinality)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            [MinAttribute] = cardinality.Min.ToString(CultureInfo.InvariantCulture)
        };
        if (cardinality.Max is { } max)
            attributes[MaxAttribute] = max.ToString(CultureInfo.InvariantCulture);
        return attributes;
    }

    private static Dictionary<string, string> CreateCardinalityAttributes(NodeSlotCardinality cardinality, IEnumerable<NodeType> allowedTypes)
    {
        var attributes = CreateCardinalityAttributes(cardinality);
        attributes[AllowedTypePathsAttribute] = JsonSerializer.Serialize(
            allowedTypes.Select(static type => type.GlobalId.ToString()));
        return attributes;
    }

    private static IEnumerable<NodeType> ReadStoredNodeTypes(
        IDictionary<string, string> attributes,
        string attributeName,
        Node? nodeTypesRoot,
        Func<IEnumerable<NodeType>> fallback)
    {
        if (!attributes.TryGetValue(attributeName, out var serializedPaths))
            return fallback();
        if (nodeTypesRoot is null)
            return [];

        var paths = attributeName == NodeTypePathAttribute
            ? [serializedPaths]
            : JsonSerializer.Deserialize<string[]>(serializedPaths)
                ?? throw new InvalidOperationException($"Dynamic node definition has invalid '{attributeName}'.");
        return paths.Select(path => FindTypeByPath(nodeTypesRoot, path)
            ?? throw new InvalidOperationException(
                $"Dynamic node definition references unknown node type '{path}'."))
            .Select(static node => new NodeType(node.Backing));
    }

    private static Node? FindTypeByPath(Node nodeTypesRoot, string path)
    {
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => new NodeLocalId(segment))
            .ToArray();
        var rootSegments = nodeTypesRoot.GlobalId.ToArray();
        if (segments.Length <= rootSegments.Length
            || !rootSegments.SequenceEqual(segments.Take(rootSegments.Length)))
            return null;

        var current = nodeTypesRoot;
        for (var index = rootSegments.Length; index < segments.Length; index++) {
            var expectedId = new InternalId(segments.Take(index + 1));
            current = current.Nodes.SingleOrDefault(node => node.GlobalId == expectedId);
            if (current is null)
                return null;
        }

        return IsNodeType(current, nodeTypesRoot) ? current : null;
    }

    private static NodeFieldValueKind ReadValueKind(IDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(ValueKindAttribute, out var value)
            || !Enum.TryParse<NodeFieldValueKind>(value, ignoreCase: true, out var kind))
            throw new InvalidOperationException($"Dynamic node field is missing valid '{ValueKindAttribute}'.");

        return kind;
    }

    private static Type ReadClrType(IDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(ClrTypeAttribute, out var value)
            || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Dynamic node field is missing '{ClrTypeAttribute}'.");

        var type = Type.GetType(value, throwOnError: false, ignoreCase: true);
        if (type is null)
            throw new InvalidOperationException($"Dynamic node field CLR type '{value}' cannot be resolved.");

        return type;
    }

    private static NodeSlotCardinality ReadCardinality(IDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(MinAttribute, out var minText)
            || !int.TryParse(minText, NumberStyles.None, CultureInfo.InvariantCulture, out var min))
            throw new InvalidOperationException($"Dynamic node definition is missing valid '{MinAttribute}'.");

        int? max = null;
        if (attributes.TryGetValue(MaxAttribute, out var maxText)
            && !string.IsNullOrWhiteSpace(maxText)) {
            if (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMax))
                throw new InvalidOperationException($"Dynamic node definition has invalid '{MaxAttribute}'.");
            max = parsedMax;
        }

        return new NodeSlotCardinality(min, max);
    }

    private static bool ReadBool(IDictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static GraphElementKind ReadElementKind(IDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(ElementKindAttribute, out var value)
            || !Enum.TryParse<GraphElementKind>(value, ignoreCase: true, out var kind))
            throw new InvalidOperationException($"Dynamic graph type is missing valid '{ElementKindAttribute}'.");
        return kind;
    }

    private static string SerializeClrType(Type type)
    {
        return type.AssemblyQualifiedName
            ?? type.FullName
            ?? type.Name;
    }

    private static bool IsDerivedFromField(
        NodeSlotDefinition slot,
        IReadOnlyCollection<NodeFieldDefinition> fields) {
        return fields
            .Select(static field => field.ToSlotDefinition())
            .OfType<NodeSlotDefinition>()
            .Any(fieldSlot => SlotIdentity(fieldSlot) == SlotIdentity(slot));
    }

    private static string SlotIdentity(NodeSlotDefinition slot) {
        return string.Join(
            "\0",
            slot.Name,
            slot.Cardinality.Min,
            slot.Cardinality.Max?.ToString() ?? "*",
            string.Join(",", slot.AllowedTypes
                .Select(static type => type.GlobalId.ToString())
                .Order(StringComparer.Ordinal)));
    }
}
