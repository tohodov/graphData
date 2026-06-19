using Abstractions;

namespace GraphData.Core.Models;

public sealed record EdgeEndpointDefinition(
    string Name,
    Type ClrType,
    NodeSlotCardinality Cardinality,
    bool IsCollection,
    NodeGlobalId? NodeTypeId = null);

public sealed record EdgeTypeDefinition(
    NodeGlobalId TypeId,
    IReadOnlyCollection<EdgeEndpointDefinition> Endpoints)
{
    public void EnsureSatisfiedBy(IReadOnlyCollection<InstanceNode> endpoints)
    {
        if (Endpoints.Count < 2)
            throw new InvalidOperationException($"Edge type '{TypeId}' must define at least two endpoints.");

        if (endpoints.Count != Endpoints.Count)
            throw new InvalidOperationException(
                $"Edge type '{TypeId}' expects {Endpoints.Count} endpoints, but got {endpoints.Count}.");

        var endpointInstances = endpoints.ToArray();
        var endpointDefinitions = Endpoints.ToArray();
        for (var index = 0; index < endpointDefinitions.Length; index++) {
            var definition = endpointDefinitions[index];
            if (definition.NodeTypeId is not { } nodeTypeId)
                continue;

            var instance = endpointInstances[index];
            if (!instance.AssignedTypes.Any(type => type.GlobalId == nodeTypeId))
                throw new InvalidOperationException(
                    $"Endpoint '{definition.Name}' expects node type '{nodeTypeId}', but node '{instance.GlobalId}' has another type.");
        }
    }
}

public sealed class EdgeTypeBuilder
{
    private readonly NodeGlobalId _typeId;
    private readonly Func<Type, NodeGlobalId> _resolveNodeTypeId;
    private readonly List<EdgeEndpointDefinition> _endpoints = [];

    internal EdgeTypeBuilder(NodeGlobalId typeId, Func<Type, NodeGlobalId> resolveNodeTypeId)
    {
        _typeId = typeId;
        _resolveNodeTypeId = resolveNodeTypeId;
    }

    public EdgeTypeBuilder Endpoint<TNodeType>(string name)
        where TNodeType : NodeType
    {
        return Endpoint(name, typeof(TNodeType), NodeSlotCardinality.Required(), isCollection: false, _resolveNodeTypeId(typeof(TNodeType)));
    }

    public EdgeTypeBuilder Endpoint(
        string name,
        Type nodeType,
        NodeSlotCardinality cardinality,
        bool isCollection = false,
        NodeGlobalId? nodeTypeId = null)
    {
        if (!typeof(Node).IsAssignableFrom(nodeType))
            throw new ArgumentException("Endpoint CLR type must inherit from Node.", nameof(nodeType));

        _endpoints.Add(new EdgeEndpointDefinition(
            RequireName(name),
            nodeType,
            cardinality,
            isCollection,
            nodeTypeId));
        return this;
    }

    internal EdgeTypeBuilder Endpoint(EdgeEndpointDefinition endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }

    public EdgeTypeDefinition Build()
    {
        var endpoints = _endpoints.ToArray();
        if (endpoints.Length < 2)
            throw new InvalidOperationException($"Edge type '{_typeId}' must define at least two endpoints.");

        return new EdgeTypeDefinition(_typeId, endpoints);
    }

    private static string RequireName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Endpoint name is required.", nameof(value));

        return value.Trim();
    }
}
