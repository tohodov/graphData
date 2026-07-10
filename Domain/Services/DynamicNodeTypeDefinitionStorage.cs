using System.Globalization;
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

    public static async Task WriteAsync(IGraphStorage storage, NodeTypeDefinition definition)
    {
        var definitionNode = await storage.Create(
            DefinitionNodeId,
            definition.Type.GlobalId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                [IsAbstractAttribute] = definition.IsAbstract.ToString(CultureInfo.InvariantCulture)
                , [ElementKindAttribute] = definition.ElementKind.ToString()
            }).ConfigureAwait(false);

        var fieldsNode = await storage.Create(FieldsNodeId, definitionNode.GlobalId).ConfigureAwait(false);
        foreach (var field in definition.Fields) {
            var fieldNode = await storage.Create(
                new NodeLocalId(field.Name),
                fieldsNode.GlobalId,
                CreateFieldAttributes(field)).ConfigureAwait(false);

            if (field.NodeType is not null)
                await storage.Connect(fieldNode.GlobalId, field.NodeType.GlobalId).ConfigureAwait(false);
        }

        var slotsNode = await storage.Create(SlotsNodeId, definitionNode.GlobalId).ConfigureAwait(false);
        foreach (var slot in definition.Slots.Where(slot => !IsDerivedFromField(slot, definition.Fields))) {
            var slotNode = await storage.Create(
                new NodeLocalId(slot.Name),
                slotsNode.GlobalId,
                CreateCardinalityAttributes(slot.Cardinality)).ConfigureAwait(false);

            foreach (var allowedType in slot.AllowedTypes)
                await storage.Connect(slotNode.GlobalId, allowedType.GlobalId).ConfigureAwait(false);
        }
    }

    public static NodeTypeDefinition? TryRead(NodeType typeNode)
    {
        var definitionNode = FindNeighbor(typeNode, DefinitionNodeId);
        if (definitionNode is null)
            return null;

        var nodeTypesRoot = FindNodeTypesRoot(typeNode);
        var fieldsNode = FindNeighbor(definitionNode, FieldsNodeId);
        var slotsNode = FindNeighbor(definitionNode, SlotsNodeId);
        var fields = fieldsNode is null
            ? Array.Empty<NodeFieldDefinition>()
            : ReadFields(fieldsNode, nodeTypesRoot).ToArray();
        var explicitSlots = slotsNode is null
            ? Array.Empty<NodeSlotDefinition>()
            : ReadSlots(slotsNode, nodeTypesRoot).ToArray();
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
            var allowedTypes = ReadLinkedNodeTypes(fieldNode, nodeTypesRoot, fieldsNode.GlobalId).ToArray();
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
            var allowedTypes = ReadLinkedNodeTypes(slotNode, nodeTypesRoot, slotsNode.GlobalId).ToArray();
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
        return typeNode.Nodes.FirstOrDefault(static node =>
            node.GlobalId.ToString() == "NodeTypes"
            && node.LocalId.ToString() == "NodeTypes");
    }

    private static bool IsNodeType(Node node, Node nodeTypesRoot)
    {
        return node.GlobalId != nodeTypesRoot.GlobalId
            && node.Nodes.Any(neighbor => neighbor.GlobalId == nodeTypesRoot.GlobalId);
    }

    private static Node? FindNeighbor(Node owner, NodeLocalId localId)
    {
        return owner.Nodes.FirstOrDefault(node => node.LocalId == localId);
    }

    private static Dictionary<string, string> CreateFieldAttributes(NodeFieldDefinition field)
    {
        var attributes = CreateCardinalityAttributes(field.Cardinality);
        attributes[ValueKindAttribute] = field.ValueKind.ToString();
        attributes[ClrTypeAttribute] = SerializeClrType(field.ClrType);
        attributes[IsCollectionAttribute] = field.IsCollection.ToString(CultureInfo.InvariantCulture);
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
