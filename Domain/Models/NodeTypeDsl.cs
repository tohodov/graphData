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
    IReadOnlyCollection<NodeGlobalId> AllowedTypeIds,
    NodeSlotCardinality Cardinality)
{
    public IReadOnlyCollection<TypeNode> AllowedTypes { get; init; } = [];

    public NodeSlotDefinition(
        string name,
        IReadOnlyCollection<TypeNode> allowedTypes,
        NodeSlotCardinality cardinality)
        : this(
            name,
            allowedTypes.Select(static type => type.GlobalId).ToArray(),
            cardinality) {
        AllowedTypes = allowedTypes;
    }

    public void EnsureSatisfiedBy(InstanceNode instance)
    {
        var allowedTypeIds = AllowedTypeIds.ToHashSet();
        var count = instance.NeighborInstances.Count(neighbor =>
            neighbor.AssignedTypes.Any(type => allowedTypeIds.Contains(type.GlobalId)));

        if (!Cardinality.Contains(count))
            throw new InvalidOperationException(
                $"Slot '{Name}' expects {Cardinality} linked nodes, but found {count}.");
    }
}

public sealed record NodeTypeDefinition(
    TypeNode Type,
    bool IsAbstract,
    IReadOnlyCollection<NodeSlotDefinition> Slots)
{
    public void EnsureSatisfiedBy(InstanceNode instance)
    {
        if (IsAbstract)
            throw new InvalidOperationException($"Node type '{Type.GlobalId}' is abstract.");

        if (instance.AssignedTypes.Count != 1)
            throw new InvalidOperationException($"Node '{instance.GlobalId}' must have exactly one node type.");

        var assignedType = instance.AssignedTypes.Single();
        if (assignedType.GlobalId != Type.GlobalId)
            throw new InvalidOperationException($"Node '{instance.GlobalId}' has another node type.");

        foreach (var slot in Slots)
            slot.EnsureSatisfiedBy(instance);
    }
}

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
        return RequiresSlot(name, allowedType.GlobalId, cardinality);
    }

    public NodeTypeBuilder RequiresSlot<TNodeType>(
        string name,
        NodeSlotCardinality? cardinality = null)
        where TNodeType : NodeType
    {
        return RequiresSlot(name, NodeType.GetStaticTypeId<TNodeType>(), cardinality);
    }

    public NodeTypeBuilder RequiresSlot(
        string name,
        NodeGlobalId allowedTypeId,
        NodeSlotCardinality? cardinality = null)
    {
        _slots.Add(new NodeSlotDefinition(
            RequireName(name),
            [allowedTypeId],
            cardinality ?? NodeSlotCardinality.Required()));
        return this;
    }

    public NodeTypeBuilder Slot(
        string name,
        IEnumerable<TypeNode> allowedTypes,
        NodeSlotCardinality cardinality)
    {
        return SlotByTypeIds(
            name,
            allowedTypes.Select(static type => type.GlobalId),
            cardinality);
    }

    public NodeTypeBuilder SlotByTypeIds(
        string name,
        IEnumerable<NodeGlobalId> allowedTypeIds,
        NodeSlotCardinality cardinality)
    {
        var typeIds = allowedTypeIds
            .Distinct()
            .ToArray();
        if (typeIds.Length == 0)
            throw new ArgumentException("At least one allowed node type is required.", nameof(allowedTypeIds));

        _slots.Add(new NodeSlotDefinition(RequireName(name), typeIds, cardinality));
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
