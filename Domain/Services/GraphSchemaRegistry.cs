using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphRuntimeTypeOptions
{
    internal List<Assembly> Assemblies { get; } = [];
    internal List<Type> Types { get; } = [];

    public GraphRuntimeTypeOptions AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assemblies.Add(assembly);
        return this;
    }

    public GraphRuntimeTypeOptions AddType<T>()
    {
        Types.Add(typeof(T));
        return this;
    }

    public GraphRuntimeTypeOptions AddType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Types.Add(type);
        return this;
    }
}

public sealed class GraphSchemaRegistry
{
    private readonly IReadOnlyDictionary<NodeLocalId, RuntimeGraphTypeDefinition> _typeDescriptors;
    private readonly ConcurrentDictionary<InternalId, NodeTypeDefinition> _definitions = new();

    private GraphSchemaRegistry(IReadOnlyCollection<RuntimeGraphTypeDefinition> types)
    {
        Types = types;
        _typeDescriptors = Types.ToDictionary(static x => x.Id);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", types.Select(static type => type.ClrType.AssemblyQualifiedName)))));
    }

    public IReadOnlyCollection<RuntimeGraphTypeDefinition> Types { get; }
    public string Fingerprint { get; }

    public static GraphSchemaRegistry Create(params Assembly[] runtimeTypeAssemblies)
    {
        var options = new GraphRuntimeTypeOptions();
        foreach (var assembly in runtimeTypeAssemblies)
            options.AddAssembly(assembly);
        return Create(options);
    }

    public static GraphSchemaRegistry Create(GraphRuntimeTypeOptions options)
    {
        var assemblies = new[] { typeof(Node).Assembly }
            .Concat(options.Assemblies)
            .Distinct()
            .ToArray();
        var candidateTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Concat(options.Types)
            .Distinct()
            .Where(static type =>
                (typeof(NodeType).IsAssignableFrom(type) || typeof(Edge).IsAssignableFrom(type))
                && type is { ContainsGenericParameters: false }
                && (type.IsPublic || type.IsNestedPublic))
            .Except([typeof(NodeType), typeof(Edge)])
            .Select(static x => new RuntimeGraphTypeDefinition(
                x,
                NodeType.CreateDefaultLocalId(x),
                typeof(Edge).IsAssignableFrom(x) ? GraphElementKind.Edge : GraphElementKind.Node))
            .ToArray();
        var duplicate = candidateTypes
            .GroupBy(static type => type.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Runtime graph type id '{duplicate.Key}' is declared more than once: {string.Join(", ", duplicate.Select(static type => type.ClrType.FullName))}.");
        
        return new GraphSchemaRegistry(candidateTypes);
    }

    public bool TryGetClrType(NodeLocalId typeId, out Type type)
    {
        if (_typeDescriptors.TryGetValue(typeId, out var descriptor)) {
            type = descriptor.ClrType;
            return true;
        }
        type = null!;
        return false;
    }

    public NodeTypeDefinition GetOrBuildDefinition(NodeType typeNode, Func<Type, NodeType> resolveType)
    {
        return _definitions.GetOrAdd(typeNode.GlobalId, id =>
        {
            var localId = typeNode.LocalId;
            if (_typeDescriptors.TryGetValue(localId, out var descriptor))
            {
                var clrType = descriptor.ClrType;
                var builder = new NodeTypeBuilder(typeNode, resolveType);
                builder.Abstract(clrType.IsAbstract);
                var hierarchyRoot = descriptor.ElementKind == GraphElementKind.Edge
                    ? typeof(Edge)
                    : typeof(NodeType);
                if (clrType.BaseType is { } baseType
                    && baseType != hierarchyRoot
                    && hierarchyRoot.IsAssignableFrom(baseType))
                    builder.Requires(resolveType(baseType));
                NodeTypeFieldDiscovery.AddDiscoveredFields(clrType, builder, resolveType);
                return builder.Build() with { ElementKind = descriptor.ElementKind };
            }

            var dynamicDefinition = DynamicNodeTypeDefinitionStorage.TryRead(typeNode);
            if (dynamicDefinition is not null)
                return dynamicDefinition;

            return new NodeTypeDefinition(
                typeNode,
                IsAbstract: false,
                Slots: Array.Empty<NodeSlotDefinition>(),
                Fields: Array.Empty<NodeFieldDefinition>());
        });
    }

    internal void RegisterDynamicDefinition(NodeTypeDefinition definition)
    {
        _definitions[definition.Type.GlobalId] = definition;
    }

    private static IReadOnlyCollection<Type> GetLoadableTypes(Assembly assembly)
    {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            return ex.Types.Where(static type => type is not null).Cast<Type>().ToArray();
        }
    }
}

public sealed record RuntimeGraphTypeDefinition(Type ClrType, NodeLocalId Id, GraphElementKind ElementKind);
