using Abstractions;

namespace GraphData.Core.Models;

public abstract class NodeType : Node
{
    protected NodeType()
    {
    }

    internal NodeType(NodeState state)
        : base(state) {
    }

    public NodeTypeDefinition Define(Action<NodeTypeBuilder>? configure = null)
    {
        var builder = new NodeTypeBuilder(this, CreateDefaultTypeId);
        Define(builder);
        configure?.Invoke(builder);
        return builder.Build();
    }

    public virtual void Define(NodeTypeBuilder type)
    {
    }

    internal NodeTypeDefinition DefineRegistered(NodeType graphType, Func<Type, InternalId> resolveTypeId)
    {
        var builder = new NodeTypeBuilder(graphType, resolveTypeId);
        NodeTypeFieldDiscovery.AddDiscoveredFields(GetType(), builder, resolveTypeId);
        Define(builder);
        return builder.Build();
    }

    internal static NodeType FromState(NodeState state) => new RuntimeNodeType(state);

    internal static InternalId CreateDefaultTypeId(Type type) =>
        new(GraphSystemNodeIds.NodeTypeRoot.Concat([new NodeLocalId(CreateDefaultLocalId(type))]));

    private static string CreateDefaultLocalId(Type type)
    {
        var name = type.Name;
        if (name.EndsWith(nameof(NodeType), StringComparison.Ordinal))
            name = name[..^nameof(NodeType).Length];
        else if (name.EndsWith(nameof(Node), StringComparison.Ordinal))
            name = name[..^nameof(Node).Length];

        return string.IsNullOrWhiteSpace(name) ? type.Name : name;
    }

    private sealed class RuntimeNodeType(NodeState state) : NodeType(state);
}
