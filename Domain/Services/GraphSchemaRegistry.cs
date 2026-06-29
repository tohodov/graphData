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
    private readonly IReadOnlyDictionary<NodeLocalId, Type> _nodeTypeDescriptors;
    private readonly IReadOnlyDictionary<Type, NodeLocalId> _nodeTypeDescriptorsByClrType;
    private readonly ConcurrentDictionary<InternalId, NodeTypeDefinition> _definitions = new();

    private GraphSchemaRegistry(IReadOnlyCollection<RuntimeGraphTypeDefinition> types)
    {
        Types = types;
        _nodeTypeDescriptors = Types.ToDictionary(static x => x.Id, static x => x.ClrType);
        _nodeTypeDescriptorsByClrType = Types.ToDictionary(static x => x.ClrType, static x => x.Id);
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
                typeof(NodeType).IsAssignableFrom(type)
                && type is { IsAbstract: false, ContainsGenericParameters: false }
                && (type.IsPublic || type.IsNestedPublic))
            .Except([typeof(NodeType)])
            .Select(static x => new RuntimeGraphTypeDefinition(x, NodeType.CreateDefaultLocalId(x)))
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
        return _nodeTypeDescriptors.TryGetValue(typeId, out type!);
    }

    public NodeLocalId GetNodeTypeId(Type type)
    {
        if (_nodeTypeDescriptorsByClrType.TryGetValue(type, out var id))
            return id;

        throw new InvalidOperationException($"CLR type '{type.FullName}' is not a registered node type.");
    }

    public NodeTypeDefinition GetOrBuildDefinition(NodeType typeNode, Func<Type, InternalId> resolveTypeId)
    {
        return _definitions.GetOrAdd(typeNode.GlobalId, id =>
        {
            var localId = typeNode.LocalId;
            if (TryGetClrType(localId, out var clrType))
            {
                var builder = new NodeTypeBuilder(typeNode, resolveTypeId);
                NodeTypeFieldDiscovery.AddDiscoveredFields(clrType, builder, resolveTypeId);
                return builder.Build();
            }

            var dynamicBuilder = new NodeTypeBuilder(typeNode, resolveTypeId);
            return dynamicBuilder.Build();
        });
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

public sealed record RuntimeGraphTypeDefinition(Type ClrType, NodeLocalId Id);
