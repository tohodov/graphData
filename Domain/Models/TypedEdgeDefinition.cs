using Abstractions;

namespace GraphData.Core.Models;

public sealed record TypedEdgeEndpointDefinition(
    string Name,
    Type ClrType,
    NodeSlotCardinality Cardinality,
    bool IsCollection,
    NodeType? NodeType = null);

public sealed record TypedEdgeDefinition(
    NodeTypeDefinition NodeType,
    IReadOnlyCollection<TypedEdgeEndpointDefinition> Endpoints)
{
    public NodeType Type => NodeType.Type;

    public void EnsureSatisfiedBy(IReadOnlyCollection<InstanceNode> endpoints)
    {
        if (Endpoints.Count < 2)
            throw new InvalidOperationException($"Typed edge node type '{Type.GlobalId}' must define at least two endpoints.");

        if (endpoints.Count != Endpoints.Count)
            throw new InvalidOperationException(
                $"Typed edge node type '{Type.GlobalId}' expects {Endpoints.Count} endpoints, but got {endpoints.Count}.");

        var endpointInstances = endpoints.ToArray();
        var endpointDefinitions = Endpoints.ToArray();
        for (var index = 0; index < endpointDefinitions.Length; index++) {
            var definition = endpointDefinitions[index];
            if (definition.NodeType is not { } nodeType)
                continue;

            var instance = endpointInstances[index];
            if (!instance.Nodes.Any(type => type.GlobalId == nodeType.GlobalId))
                throw new InvalidOperationException($"Endpoint '{definition.Name}' expects node type '{nodeType.GlobalId}', but node '{instance.GlobalId}' has another type.");
        }
    }

    public static bool TryCreate(NodeTypeDefinition nodeType, out TypedEdgeDefinition definition)
    {
        var endpoints = nodeType.Fields
            .Where(static field => field.ValueKind == NodeFieldValueKind.Node)
            .Where(static field => typeof(Node).IsAssignableFrom(field.ClrType))
            .Select(static field => new TypedEdgeEndpointDefinition(
                field.Name,
                field.ClrType,
                field.Cardinality,
                field.IsCollection,
                field.NodeType))
            .ToArray();

        if (endpoints.Length < 2) {
            definition = default!;
            return false;
        }

        definition = new TypedEdgeDefinition(nodeType, endpoints);
        return true;
    }

    public static TypedEdgeDefinition Create(NodeTypeDefinition nodeType)
    {
        if (TryCreate(nodeType, out var definition))
            return definition;

        throw new InvalidOperationException(
            $"Node type '{nodeType.Type.GlobalId}' must define at least two node endpoints to be used as a typed edge.");
    }
}
