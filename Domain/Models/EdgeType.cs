using Abstractions;

namespace GraphData.Core.Models;

public abstract class EdgeType : Edge
{
    protected EdgeType()
    {
    }

    internal EdgeType(EdgeState state)
        : base(state) {
    }

    public EdgeTypeDefinition Define(Action<EdgeTypeBuilder>? configure = null)
    {
        var builder = new EdgeTypeBuilder(CreateDefaultTypeId(GetType()), NodeType.CreateDefaultTypeId);
        Define(builder);
        configure?.Invoke(builder);
        return builder.Build();
    }

    public virtual void Define(EdgeTypeBuilder type)
    {
    }

    internal EdgeTypeDefinition DefineRegistered(
        InternalId graphTypeId,
        Func<Type, InternalId> resolveNodeTypeId)
    {
        var builder = new EdgeTypeBuilder(graphTypeId, resolveNodeTypeId);
        EdgeTypeEndpointDiscovery.AddDiscoveredEndpoints(GetType(), builder, resolveNodeTypeId);
        Define(builder);
        return builder.Build();
    }

    internal static InternalId CreateDefaultTypeId(Type type) =>
        new(GraphSystemNodeIds.EdgeTypeRoot.Concat([new NodeLocalId(CreateDefaultLocalId(type))]));

    private static string CreateDefaultLocalId(Type type)
    {
        var name = type.Name;
        if (name.EndsWith(nameof(EdgeType), StringComparison.Ordinal))
            name = name[..^nameof(EdgeType).Length];
        else if (name.EndsWith(nameof(Edge), StringComparison.Ordinal))
            name = name[..^nameof(Edge).Length];

        return string.IsNullOrWhiteSpace(name) ? type.Name : name;
    }

}
