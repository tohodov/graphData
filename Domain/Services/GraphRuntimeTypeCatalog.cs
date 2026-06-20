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

internal sealed class GraphRuntimeTypeCatalog
{
    private readonly IReadOnlyDictionary<InternalId, RuntimeGraphTypeDefinition> _nodeTypeDescriptors;
    private readonly IReadOnlyDictionary<Type, RuntimeGraphTypeDefinition> _nodeTypeDescriptorsByClrType;
    private readonly IReadOnlyDictionary<InternalId, RuntimeGraphTypeDefinition> _edgeTypeDescriptors;//TODO удалить
    private readonly IReadOnlyDictionary<Type, RuntimeGraphTypeDefinition> _edgeTypeDescriptorsByClrType;

    private GraphRuntimeTypeCatalog(IReadOnlyCollection<RuntimeGraphTypeDefinition> types)
    {
        Types = types;
        _nodeTypeDescriptors = Types
            .Where(static type => type.NodeTypeDescriptor is not null)
            .ToDictionary(static type => type.TypeId);
        _nodeTypeDescriptorsByClrType = Types
            .Where(static type => type.NodeTypeDescriptor is not null)
            .ToDictionary(static type => type.ClrType);
        _edgeTypeDescriptors = Types
            .Where(static type => type.EdgeTypeDescriptor is not null)
            .ToDictionary(static type => type.TypeId);
        _edgeTypeDescriptorsByClrType = Types
            .Where(static type => type.EdgeTypeDescriptor is not null)
            .ToDictionary(static type => type.ClrType);
        Fingerprint = CreateFingerprint(Types);
    }

    public IReadOnlyCollection<RuntimeGraphTypeDefinition> Types { get; }

    public string Fingerprint { get; }

    public static GraphRuntimeTypeCatalog Create() =>
        Create([]);

    public static GraphRuntimeTypeCatalog Create(params Assembly[] runtimeTypeAssemblies)
    {
        var options = new GraphRuntimeTypeOptions();
        foreach (var assembly in runtimeTypeAssemblies)
            options.AddAssembly(assembly);
        return Create(options);
    }

    public static GraphRuntimeTypeCatalog Create(GraphRuntimeTypeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var assemblies = new[] { typeof(Node).Assembly }
            .Concat(options.Assemblies)
            .Distinct()
            .ToArray();
        var candidateTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Concat(options.Types)
            .Distinct()
            .Where(static type => type is { IsAbstract: false, ContainsGenericParameters: false })
            .Select(CreateRuntimeTypeDefinition)
            .Where(static definition => definition is not null)
            .Cast<RuntimeGraphTypeDefinition>()
            .OrderBy(static type => type.Element, StringComparer.Ordinal)
            .ThenBy(static type => type.TypeId.ToString(), StringComparer.Ordinal)
            .ThenBy(static type => type.ClrType.FullName, StringComparer.Ordinal)
            .ToArray();

        ThrowForDuplicateTypeIds(candidateTypes);
        return new GraphRuntimeTypeCatalog(candidateTypes);
    }

    public bool TryCreateNodeTypeDefinition(
        InternalId typeId,
        NodeType typeNode,
        out NodeTypeDefinition definition)
    {
        if (_nodeTypeDescriptors.TryGetValue(typeId, out var runtimeType)
            && runtimeType.NodeTypeDescriptor is { } descriptor) {
            definition = descriptor.DefineRegistered(typeNode, GetNodeTypeId);
            return true;
        }

        definition = default!;
        return false;
    }

    public InternalId GetNodeTypeId(Type type)
    {
        if (_nodeTypeDescriptorsByClrType.TryGetValue(type, out var runtimeType))
            return runtimeType.TypeId;

        throw new InvalidOperationException($"CLR type '{type.FullName}' is not a registered node type.");
    }

    public bool TryCreateEdgeTypeDefinition(
        InternalId typeId,
        out EdgeTypeDefinition definition)
    {
        if (_edgeTypeDescriptors.TryGetValue(typeId, out var runtimeType)
            && runtimeType.EdgeTypeDescriptor is { } descriptor) {
            definition = descriptor.DefineRegistered(typeId, GetNodeTypeId);
            return true;
        }

        definition = default!;
        return false;
    }

    public InternalId GetEdgeTypeId(Type type)
    {
        if (_edgeTypeDescriptorsByClrType.TryGetValue(type, out var runtimeType))
            return runtimeType.TypeId;

        throw new InvalidOperationException($"CLR type '{type.FullName}' is not a registered edge type.");
    }

    private static RuntimeGraphTypeDefinition? CreateRuntimeTypeDefinition(Type type)
    {
        if (type.DeclaringType == typeof(NodeType))
            return null;
        if (type.DeclaringType == typeof(EdgeType))
            return null;

        if (typeof(Node).IsAssignableFrom(type) && typeof(IGraphNodeType).IsAssignableFrom(type))
            return new RuntimeGraphTypeDefinition(
                type,
                GetStaticTypeId(type),
                "node",
                GraphSystemNodeIds.NodeTypeRoot,
                null,
                null);

        if (typeof(NodeType).IsAssignableFrom(type))
            return new RuntimeGraphTypeDefinition(
                type,
                NodeType.CreateDefaultTypeId(type),
                "node",
                GraphSystemNodeIds.NodeTypeRoot,
                CreateNodeTypeDescriptor(type),
                null);

        if (typeof(EdgeType).IsAssignableFrom(type))
            return new RuntimeGraphTypeDefinition(
                type,
                EdgeType.CreateDefaultTypeId(type),
                "relation",
                GraphSystemNodeIds.NodeTypeRoot,
                null,
                CreateEdgeTypeDescriptor(type));

        return null;
    }

    private static NodeType CreateNodeTypeDescriptor(Type type)
    {
        try {
            return Activator.CreateInstance(type, nonPublic: true) as NodeType
                ?? throw new InvalidOperationException($"Runtime node type descriptor '{type.FullName}' could not be created.");
        } catch (MissingMethodException ex) {
            throw new InvalidOperationException(
                $"Runtime node type descriptor '{type.FullName}' must expose a parameterless constructor.",
                ex);
        }
    }

    private static EdgeType CreateEdgeTypeDescriptor(Type type)
    {
        try {
            return Activator.CreateInstance(type, nonPublic: true) as EdgeType
                ?? throw new InvalidOperationException($"Runtime edge type descriptor '{type.FullName}' could not be created.");
        } catch (MissingMethodException ex) {
            throw new InvalidOperationException(
                $"Runtime edge type descriptor '{type.FullName}' must expose a parameterless constructor.",
                ex);
        }
    }

    private static InternalId GetStaticTypeId(Type type)
    {
        var property = type.GetProperty(
            "StaticTypeId",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property?.GetValue(null) is InternalId id)
            return id;

        throw new InvalidOperationException($"Runtime graph type '{type.FullName}' must expose a public static StaticTypeId property.");
    }

    private static IReadOnlyCollection<Type> GetLoadableTypes(Assembly assembly)
    {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            return ex.Types.Where(static type => type is not null).Cast<Type>().ToArray();
        }
    }

    private static void ThrowForDuplicateTypeIds(IReadOnlyCollection<RuntimeGraphTypeDefinition> types)
    {
        var duplicate = types
            .GroupBy(static type => type.TypeId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is null)
            return;

        var owners = string.Join(", ", duplicate.Select(static type => type.ClrType.FullName));
        throw new InvalidOperationException(
            $"Runtime graph type id '{duplicate.Key}' is declared more than once: {owners}.");
    }

    private static string CreateFingerprint(IEnumerable<RuntimeGraphTypeDefinition> types)
    {
        var source = string.Join(
            "\n",
            types.Select(static type => $"{type.Element}:{type.TypeId}:{type.ClrType.AssemblyQualifiedName}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}

internal sealed record RuntimeGraphTypeDefinition(
    Type ClrType,
    InternalId TypeId,
    string Element,
    InternalId RootId,
    NodeType? NodeTypeDescriptor,
    EdgeType? EdgeTypeDescriptor);
