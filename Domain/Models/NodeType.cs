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

    internal NodeTypeDefinition DefineRegistered(NodeType graphType, Func<Type, NodeGlobalId> resolveTypeId)
    {
        var builder = new NodeTypeBuilder(graphType, resolveTypeId);
        NodeTypeFieldDiscovery.AddDiscoveredFields(GetType(), builder, resolveTypeId);
        Define(builder);
        return builder.Build();
    }

    internal static NodeType FromState(NodeState state) => new RuntimeNodeType(state);

    internal static NodeGlobalId CreateDefaultTypeId(Type type) =>
        new(GraphSystemNodeIds.NodeTypeRoot.Concat([new NodeLocalId(CreateDefaultLocalId(type))]));

    internal static bool IsNodeTypeId(NodeGlobalId id)
    {
        var idSegments = id.ToArray();
        var rootSegments = GraphSystemNodeIds.NodeTypeRoot.ToArray();
        if (idSegments.Length <= rootSegments.Length)
            return false;

        for (var index = 0; index < rootSegments.Length; index++)
            if (idSegments[index] != rootSegments[index])
                return false;

        return true;
    }

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
