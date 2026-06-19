using Abstractions;

namespace GraphData.Core.Models;

public readonly record struct NodeTypeId(NodeGlobalId Value)
{
    public override string ToString() => Value.ToString();

    public static implicit operator NodeGlobalId(NodeTypeId id) => id.Value;

    public static implicit operator NodeTypeId(NodeGlobalId id) => new(id);
}

public readonly record struct NodeSlotCardinality(int Min, int? Max)
{
    public static NodeSlotCardinality Optional() => new(0, 1);

    public static NodeSlotCardinality Required() => new(1, 1);

    public static NodeSlotCardinality Many(int min = 0) => new(min, null);

    public bool Contains(int count) => count >= Min && (Max is null || count <= Max.Value);

    public override string ToString() => Max is null ? $"{Min}..*" : $"{Min}..{Max}";
}

public sealed record NodeSlotDefinition(
    string Name,
    IReadOnlyCollection<NodeTypeId> AllowedTypes,
    NodeSlotCardinality Cardinality);

public sealed record NodeTypeDefinition(
    NodeTypeId Id,
    string Label,
    bool IsAbstract,
    IReadOnlyCollection<NodeSlotDefinition> Slots)
{
    public static NodeTypeDefinition Define(NodeGlobalId id, Action<NodeTypeBuilder>? configure = null)
    {
        var builder = new NodeTypeBuilder(id);
        configure?.Invoke(builder);
        return builder.Build();
    }
}

public sealed class NodeTypeBuilder
{
    private readonly NodeTypeId _id;
    private readonly List<NodeSlotDefinition> _slots = [];
    private string? _label;
    private bool _isAbstract;

    public NodeTypeBuilder(NodeGlobalId id)
    {
        _id = new NodeTypeId(id);
    }

    public NodeTypeBuilder Label(string label)
    {
        _label = label;
        return this;
    }

    public NodeTypeBuilder Abstract(bool value = true)
    {
        _isAbstract = value;
        return this;
    }

    public NodeTypeBuilder RequiresSlot(
        string name,
        NodeGlobalId allowedType,
        NodeSlotCardinality? cardinality = null)
    {
        _slots.Add(new NodeSlotDefinition(
            RequireName(name),
            [new NodeTypeId(allowedType)],
            cardinality ?? NodeSlotCardinality.Required()));
        return this;
    }

    public NodeTypeBuilder Slot(
        string name,
        IEnumerable<NodeGlobalId> allowedTypes,
        NodeSlotCardinality cardinality)
    {
        var ids = allowedTypes
            .Select(static id => new NodeTypeId(id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            throw new ArgumentException("At least one allowed node type is required.", nameof(allowedTypes));

        _slots.Add(new NodeSlotDefinition(RequireName(name), ids, cardinality));
        return this;
    }

    public NodeTypeDefinition Build() => new(
        _id,
        _label ?? _id.Value.ToString(),
        _isAbstract,
        _slots.ToArray());

    private static string RequireName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Slot name is required.", nameof(value));

        return value.Trim();
    }
}

public sealed class NodeTypeSchema
{
    private readonly IReadOnlyDictionary<NodeTypeId, NodeTypeDefinition> _nodeTypes;

    private NodeTypeSchema(IReadOnlyDictionary<NodeTypeId, NodeTypeDefinition> nodeTypes)
    {
        _nodeTypes = nodeTypes;
    }

    public IReadOnlyCollection<NodeTypeDefinition> NodeTypes => _nodeTypes.Values.ToArray();

    public bool Contains(NodeTypeId id) => _nodeTypes.ContainsKey(id);

    public NodeTypeDefinition? Find(NodeTypeId id) =>
        _nodeTypes.TryGetValue(id, out var definition) ? definition : null;

    public static NodeTypeSchema Create(IEnumerable<NodeTypeDefinition> definitions)
    {
        return new NodeTypeSchema(definitions
            .GroupBy(static definition => definition.Id)
            .Select(static group => group.Last())
            .ToDictionary(static definition => definition.Id));
    }
}

public sealed record TypedNodeInstance(
    NodeGlobalId Id,
    IReadOnlyCollection<NodeTypeId> AssignedTypes,
    IReadOnlyCollection<TypedNodeNeighbor> Neighbors)
{
    public NodeTypeId? SingleAssignedType => AssignedTypes.Count == 1 ? AssignedTypes.Single() : null;
}

public sealed record TypedNodeNeighbor(
    NodeGlobalId Id,
    IReadOnlyCollection<NodeTypeId> AssignedTypes);

public enum NodeTypeDiagnosticSeverity
{
    Error,
    Warning
}

public sealed record NodeTypeDiagnostic(
    NodeTypeDiagnosticSeverity Severity,
    string Code,
    string Message,
    NodeGlobalId NodeId,
    string? SlotName = null);

public sealed record NodeTypeValidationResult(IReadOnlyCollection<NodeTypeDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => diagnostic.Severity != NodeTypeDiagnosticSeverity.Error);

    public static NodeTypeValidationResult Ok { get; } = new([]);

    public string ToUserMessage() => string.Join(Environment.NewLine, Diagnostics.Select(static diagnostic => diagnostic.Message));
}

public static class NodeTypeValidator
{
    public static NodeTypeValidationResult Validate(NodeTypeSchema schema, TypedNodeInstance instance)
    {
        var diagnostics = new List<NodeTypeDiagnostic>();
        if (instance.AssignedTypes.Count == 0) {
            diagnostics.Add(Error("node-type.missing", $"Node '{instance.Id}' has no assigned node type.", instance.Id));
            return new NodeTypeValidationResult(diagnostics);
        }

        if (instance.AssignedTypes.Count > 1)
            diagnostics.Add(Error("node-type.multiple", $"Node '{instance.Id}' has multiple assigned node types.", instance.Id));

        foreach (var typeId in instance.AssignedTypes)
            if (!schema.Contains(typeId))
                diagnostics.Add(Error("node-type.unknown", $"Node '{instance.Id}' uses unknown node type '{typeId}'.", instance.Id));

        var definition = instance.SingleAssignedType is { } singleType ? schema.Find(singleType) : null;
        if (definition is null)
            return new NodeTypeValidationResult(diagnostics);

        if (definition.IsAbstract)
            diagnostics.Add(Error("node-type.abstract", $"Node '{instance.Id}' cannot use abstract node type '{definition.Id}'.", instance.Id));

        foreach (var slot in definition.Slots) {
            var count = instance.Neighbors.Count(neighbor => neighbor.AssignedTypes.Any(slot.AllowedTypes.Contains));
            if (slot.Cardinality.Contains(count))
                continue;

            diagnostics.Add(Error(
                "node-type.slot-cardinality",
                $"Node '{instance.Id}' slot '{slot.Name}' expects {slot.Cardinality} nodes of types {string.Join(", ", slot.AllowedTypes)}, but found {count}.",
                instance.Id,
                slot.Name));
        }

        return diagnostics.Count == 0
            ? NodeTypeValidationResult.Ok
            : new NodeTypeValidationResult(diagnostics);
    }

    private static NodeTypeDiagnostic Error(string code, string message, NodeGlobalId nodeId, string? slotName = null) =>
        new(NodeTypeDiagnosticSeverity.Error, code, message, nodeId, slotName);
}
