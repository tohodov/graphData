using System.Reflection;
using Abstractions;

namespace GraphData.Core.Models;

public abstract class NodeType
{
    protected NodeType()
    {
    }

    public NodeGlobalId TypeId => GetStaticTypeId(GetType());

    public NodeTypeDefinition Define(TypeNode type)
    {
        var builder = new NodeTypeBuilder(type);
        Define(builder);
        return builder.Build();
    }

    public virtual void Define(NodeTypeBuilder type)
    {
    }

    public static NodeGlobalId GetStaticTypeId<TNodeType>()
        where TNodeType : NodeType =>
        GetStaticTypeId(typeof(TNodeType));

    internal static NodeGlobalId GetStaticTypeId(Type type)
    {
        var property = type.GetProperty(
            "StaticTypeId",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property?.GetValue(null) is NodeGlobalId id)
            return id;

        throw new InvalidOperationException(
            $"Runtime node type descriptor '{type.FullName}' must expose a public static StaticTypeId property.");
    }
}
