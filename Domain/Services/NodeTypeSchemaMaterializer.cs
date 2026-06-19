using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal sealed class NodeTypeSchemaMaterializer(IGraphStorage storage)
{
    private const int TypeRootTraversalDepth = 32;

    public async Task<ServiceResult<NodeTypeSchema>> LoadAsync()
    {
        var subgraphResult = await storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = [GraphSystemNodeIds.NodeTypeRoot],
            MaxDepth = TypeRootTraversalDepth
        }).ConfigureAwait(false);
        if (subgraphResult.Status != ServiceResultStatus.Ok || subgraphResult.Value is null)
            return ServiceResult<NodeTypeSchema>.From(subgraphResult);

        var definitions = subgraphResult.Value.Nodes
            .Select(static node => node.GlobalId)
            .Where(static id => IsChildOf(id, GraphSystemNodeIds.NodeTypeRoot))
            .Select(static id => NodeTypeDefinition.Define(id, builder => builder.Label(LocalIdOf(id))))
            .Concat(RuntimeNodeTypes())
            .ToArray();

        return ServiceResult<NodeTypeSchema>.Ok(NodeTypeSchema.Create(definitions));
    }

    public async Task<ServiceResult<TypedNodeInstance>> MaterializeAsync(
        NodeGlobalId nodeId,
        NodeTypeSchema schema)
    {
        var nodeResult = await storage.Get(nodeId).ConfigureAwait(false);
        if (nodeResult.Status != ServiceResultStatus.Ok || nodeResult.Value is null)
            return ServiceResult<TypedNodeInstance>.From(nodeResult);

        var assignedTypes = await ResolveAssignedTypesAsync(nodeResult.Value, schema).ConfigureAwait(false);
        if (assignedTypes.Status != ServiceResultStatus.Ok || assignedTypes.Value is null)
            return ServiceResult<TypedNodeInstance>.From(assignedTypes);

        var connectedResult = await storage.GetConnectedNodesAsync(nodeResult.Value).ConfigureAwait(false);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<TypedNodeInstance>.From(connectedResult);

        var neighbors = new List<TypedNodeNeighbor>();
        foreach (var neighbor in connectedResult.Value.Where(node => !IsNodeTypeId(node.GlobalId, schema))) {
            var neighborTypes = await ResolveAssignedTypesAsync(neighbor, schema).ConfigureAwait(false);
            if (neighborTypes.Status != ServiceResultStatus.Ok || neighborTypes.Value is null)
                return ServiceResult<TypedNodeInstance>.From(neighborTypes);

            neighbors.Add(new TypedNodeNeighbor(neighbor.GlobalId, neighborTypes.Value));
        }

        return ServiceResult<TypedNodeInstance>.Ok(new TypedNodeInstance(
            nodeId,
            assignedTypes.Value,
            neighbors));
    }

    private async Task<ServiceResult<IReadOnlyCollection<NodeTypeId>>> ResolveAssignedTypesAsync(
        NodeState node,
        NodeTypeSchema schema)
    {
        var connectedResult = await storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
        if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null)
            return ServiceResult<IReadOnlyCollection<NodeTypeId>>.From(connectedResult);

        var assigned = connectedResult.Value
            .Where(neighbor => IsNodeTypeId(neighbor.GlobalId, schema))
            .Select(static neighbor => new NodeTypeId(neighbor.GlobalId))
            .Distinct()
            .ToArray();

        return ServiceResult<IReadOnlyCollection<NodeTypeId>>.Ok(assigned);
    }

    private static bool IsNodeTypeId(NodeGlobalId id, NodeTypeSchema schema) =>
        schema.Contains(new NodeTypeId(id)) || IsChildOf(id, GraphSystemNodeIds.NodeTypeRoot);

    private static IEnumerable<NodeTypeDefinition> RuntimeNodeTypes()
    {
        yield return NodeTypeDefinition.Define(GraphBaseTypeIds.NodeType, builder => builder.Label("Type"));
        yield return NodeTypeDefinition.Define(GraphBaseTypeIds.NodeInstance, builder => builder.Label("Instance"));
    }

    private static bool IsChildOf(NodeGlobalId id, NodeGlobalId root)
    {
        var idSegments = id.ToArray();
        var rootSegments = root.ToArray();
        if (idSegments.Length <= rootSegments.Length)
            return false;

        for (var index = 0; index < rootSegments.Length; index++)
            if (idSegments[index] != rootSegments[index])
                return false;

        return true;
    }

    private static string LocalIdOf(NodeGlobalId id)
    {
        var segments = id.ToArray();
        return segments.Length == 0 ? string.Empty : segments[^1].ToString();
    }
}
