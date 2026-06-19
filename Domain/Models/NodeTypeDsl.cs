using Abstractions;

namespace GraphData.Core.Models;

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
    IReadOnlyCollection<TypeNode> AllowedTypes,
    NodeSlotCardinality Cardinality);

public sealed record NodeTypeDefinition(
    TypeNode Type,
    bool IsAbstract,
    IReadOnlyCollection<NodeSlotDefinition> Slots);

public sealed class NodeTypeBuilder
{
    private readonly TypeNode _type;
    private readonly List<NodeSlotDefinition> _slots = [];
    private bool _isAbstract;

    internal NodeTypeBuilder(TypeNode type)
    {
        _type = type;
    }

    public NodeTypeBuilder Abstract(bool value = true)
    {
        _isAbstract = value;
        return this;
    }

    public NodeTypeBuilder RequiresSlot(
        string name,
        TypeNode allowedType,
        NodeSlotCardinality? cardinality = null)
    {
        _slots.Add(new NodeSlotDefinition(
            RequireName(name),
            [allowedType],
            cardinality ?? NodeSlotCardinality.Required()));
        return this;
    }

    public NodeTypeBuilder Slot(
        string name,
        IEnumerable<TypeNode> allowedTypes,
        NodeSlotCardinality cardinality)
    {
        var types = allowedTypes
            .GroupBy(static type => type.GlobalId)
            .Select(static group => group.First())
            .ToArray();
        if (types.Length == 0)
            throw new ArgumentException("At least one allowed node type is required.", nameof(allowedTypes));

        _slots.Add(new NodeSlotDefinition(RequireName(name), types, cardinality));
        return this;
    }

    public NodeTypeDefinition Build() => new(
        _type,
        _isAbstract,
        _slots.ToArray());

    private static string RequireName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Slot name is required.", nameof(value));

        return value.Trim();
    }
}

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
    public static NodeTypeValidationResult Validate(NodeTypeDefinition definition, InstanceNode instance)
    {
        var diagnostics = new List<NodeTypeDiagnostic>();
        var assignedTypes = instance.AssignedTypes;
        if (assignedTypes.Count == 0) {
            diagnostics.Add(Error("node-type.missing", $"Node '{instance.GlobalId}' has no assigned node type.", instance.GlobalId));
            return new NodeTypeValidationResult(diagnostics);
        }

        if (assignedTypes.Count > 1)
            diagnostics.Add(Error("node-type.multiple", $"Node '{instance.GlobalId}' has multiple assigned node types.", instance.GlobalId));

        if (assignedTypes.All(type => type.GlobalId != definition.Type.GlobalId))
            diagnostics.Add(Error(
                "node-type.not-assigned",
                $"Node '{instance.GlobalId}' is not assigned to node type '{definition.Type.GlobalId}'.",
                instance.GlobalId));

        if (definition.IsAbstract)
            diagnostics.Add(Error("node-type.abstract", $"Node '{instance.GlobalId}' cannot use abstract node type '{definition.Type.GlobalId}'.", instance.GlobalId));

        foreach (var slot in definition.Slots) {
            var allowed = slot.AllowedTypes.Select(static type => type.GlobalId).ToHashSet();
            var count = instance.NeighborInstances.Count(neighbor => neighbor.AssignedTypes.Any(type => allowed.Contains(type.GlobalId)));
            if (slot.Cardinality.Contains(count))
                continue;

            diagnostics.Add(Error(
                "node-type.slot-cardinality",
                $"Node '{instance.GlobalId}' slot '{slot.Name}' expects {slot.Cardinality} nodes of types {string.Join(", ", slot.AllowedTypes.Select(static type => type.GlobalId))}, but found {count}.",
                instance.GlobalId,
                slot.Name));
        }

        return diagnostics.Count == 0
            ? NodeTypeValidationResult.Ok
            : new NodeTypeValidationResult(diagnostics);
    }

    private static NodeTypeDiagnostic Error(string code, string message, NodeGlobalId nodeId, string? slotName = null) =>
        new(NodeTypeDiagnosticSeverity.Error, code, message, nodeId, slotName);
}
