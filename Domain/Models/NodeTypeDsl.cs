using Abstractions;

namespace GraphData.Core.Models;

public enum NodeFieldValueKind
{
    Node,
    Primitive
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
    IReadOnlyCollection<NodeType> AllowedTypes,
    NodeSlotCardinality Cardinality)
{
    public void EnsureSatisfiedBy(InstanceNode instance)
    {
        var allowedTypeIds = AllowedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        var assignedTypeIds = instance.AssignedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        var count = instance.Nodes.Count(neighbor =>
            !assignedTypeIds.Contains(neighbor.GlobalId)
            && neighbor.Nodes.Any(type => allowedTypeIds.Contains(type.GlobalId)));
        if (!Cardinality.Contains(count))
            throw new InvalidOperationException($"Slot '{Name}' expects {Cardinality} linked nodes, but found {count}.");
    }
}

public sealed record NodeFieldDefinition(
    string Name,
    NodeFieldValueKind ValueKind,
    Type ClrType,
    NodeSlotCardinality Cardinality,
    bool IsCollection,
    NodeType? NodeType = null)
{
    internal NodeSlotDefinition? ToSlotDefinition() =>
        ValueKind == NodeFieldValueKind.Node && NodeType is { } nodeType
            ? new NodeSlotDefinition(Name, [nodeType], Cardinality)
            : null;
}

public sealed record NodeTypeDefinition(
    NodeType Type,
    bool IsAbstract,
    IReadOnlyCollection<NodeSlotDefinition> Slots,
    IReadOnlyCollection<NodeFieldDefinition> Fields)
{
    public IReadOnlyCollection<NodeType> RequiredTypes { get; init; } = Array.Empty<NodeType>();

    public void EnsureSatisfiedBy(InstanceNode instance)
    {
        var assignedTypeIds = instance.AssignedTypes
            .Select(static type => type.GlobalId)
            .ToHashSet();
        var missingRequiredTypes = RequiredTypes
            .Where(type => !assignedTypeIds.Contains(type.GlobalId))
            .Select(static type => type.GlobalId.ToString())
            .ToArray();
        if (missingRequiredTypes.Length > 0)
            throw new InvalidOperationException(
                $"Node '{instance.GlobalId}' is missing required graph types: {string.Join(", ", missingRequiredTypes)}.");

        foreach (var slot in Slots)
            slot.EnsureSatisfiedBy(instance);
    }
}

public sealed class NodeTypeBuilder
{
    private readonly NodeType _type;
    private readonly Func<Type, NodeType> _resolveType;
    private readonly List<NodeSlotDefinition> _slots = [];
    private readonly List<NodeFieldDefinition> _fields = [];
    private readonly List<NodeType> _requiredTypes = [];
    private bool _isAbstract;

    internal NodeTypeBuilder(NodeType type, Func<Type, NodeType> resolveType)
    {
        _type = type;
        _resolveType = resolveType;
    }

    public NodeTypeBuilder Abstract(bool value = true)
    {
        _isAbstract = value;
        return this;
    }

    public NodeTypeBuilder RequiresSlot(
        string name,
        NodeType allowedType,
        NodeSlotCardinality? cardinality = null)
    {
        _slots.Add(new NodeSlotDefinition(
            RequireName(name),
            [allowedType],
            cardinality ?? NodeSlotCardinality.Required()));
        return this;
    }

    public NodeTypeBuilder Requires(NodeType requiredType)
    {
        ArgumentNullException.ThrowIfNull(requiredType);
        if (_requiredTypes.All(type => type.GlobalId != requiredType.GlobalId))
            _requiredTypes.Add(requiredType);
        return this;
    }

    public NodeTypeBuilder Requires<TNodeType>()
        where TNodeType : NodeType
    {
        return Requires(_resolveType(typeof(TNodeType)));
    }

    public NodeTypeBuilder RequiresSlot<TNodeType>(
        string name,
        NodeSlotCardinality? cardinality = null)
        where TNodeType : NodeType
    {
        return RequiresSlot(name, _resolveType(typeof(TNodeType)), cardinality);
    }

    public NodeTypeBuilder Slot(
        string name,
        IEnumerable<NodeType> allowedTypes,
        NodeSlotCardinality cardinality)
    {
        var types = allowedTypes
            .Distinct()
            .ToArray();
        if (types.Length == 0)
            throw new ArgumentException("At least one allowed node type is required.", nameof(allowedTypes));

        _slots.Add(new NodeSlotDefinition(RequireName(name), types, cardinality));
        return this;
    }

    internal NodeTypeBuilder Field(NodeFieldDefinition field)
    {
        _fields.Add(field);
        var slot = field.ToSlotDefinition();
        if (slot is not null)
            _slots.Add(slot);
        return this;
    }

    public NodeTypeDefinition Build() => new(
        _type,
        _isAbstract,
        _slots.ToArray(),
        _fields.ToArray()) {
        RequiredTypes = _requiredTypes.ToArray()
    };

    private static string RequireName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Slot name is required.", nameof(value));

        return value.Trim();
    }
}
