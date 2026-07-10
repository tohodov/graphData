using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

/// <summary>
/// Transitional carrier grammar for typed semantic edges. The semantic reader
/// is centralized here so API, MCP and active domain entities do not reproduce
/// fixed-depth traversal heuristics.
/// </summary>
internal static class TypedEdgeSubgraphCodec
{
    private static readonly NodeLocalId DefinitionLocalId = new("Definition");
    private static readonly NodeLocalId FieldsLocalId = new("Fields");

    public static async Task<NodeBacking> CreateAsync(
        IGraphStorage storage,
        NodeLocalId relationLocalId,
        TypedEdgeDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyCollection<NodeBacking>> participants)
    {
        foreach (var endpoint in definition.Endpoints) {
            if (!participants.TryGetValue(endpoint.Name, out var endpointParticipants))
                throw new InvalidOperationException($"Endpoint '{endpoint.Name}' is missing.");
            if (!endpoint.Cardinality.Contains(endpointParticipants.Count))
                throw new InvalidOperationException(
                    $"Endpoint '{endpoint.Name}' expects {endpoint.Cardinality} participants, but got {endpointParticipants.Count}.");
        }

        NodeBacking? relation = null;
        try {
            relation = await storage.Create(relationLocalId).ConfigureAwait(false);
            await storage.Connect(relation.GlobalId, definition.Type.GlobalId).ConfigureAwait(false);

            var endpointIndex = 0;
            foreach (var endpoint in definition.Endpoints) {
                var memberType = await EnsureMemberTypeAsync(storage, definition.Type, endpoint).ConfigureAwait(false);
                var endpointNode = await storage.Create(
                    EndpointOccurrenceLocalId(relationLocalId, endpointIndex++),
                    relation.GlobalId).ConfigureAwait(false);
                await storage.Connect(endpointNode.GlobalId, memberType.GlobalId).ConfigureAwait(false);
                foreach (var participant in participants[endpoint.Name].DistinctBy(static node => node.GlobalId))
                    await storage.Connect(endpointNode.GlobalId, participant.GlobalId).ConfigureAwait(false);
            }

            return relation;
        } catch {
            if (relation is not null)
                await storage.Delete(relation).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<TypedEdgeInstance> ReadAsync(
        Graph graph,
        NodeBacking relation,
        TypedEdgeDefinition definition)
    {
        if (!await HasClassifierAsync(relation, definition.Type.GlobalId).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Relation '{relation.GlobalId}' is not classified by typed edge type '{definition.Type.GlobalId}'.");

        var relationNeighbors = await relation.Nodes.ToArrayAsync().ConfigureAwait(false);
        var endpoints = new List<TypedEdgeEndpointInstance>(definition.Endpoints.Count);
        foreach (var endpoint in definition.Endpoints) {
            var endpointNodes = new List<NodeBacking>();
            foreach (var candidate in relationNeighbors) {
                if (!IsDirectChildOf(candidate.GlobalId, relation.GlobalId))
                    continue;
                if (await HasMemberClassifierAsync(candidate, endpoint.MemberTypeId).ConfigureAwait(false))
                    endpointNodes.Add(candidate);
            }
            if (endpointNodes.Count != 1)
                throw new InvalidOperationException(
                    $"Typed edge '{relation.GlobalId}' must contain exactly one endpoint instance for member '{endpoint.Name}', but found {endpointNodes.Count}.");

            var endpointNode = endpointNodes[0];
            var participantStates = (await endpointNode.Nodes.ToArrayAsync().ConfigureAwait(false))
                .Where(node => node.GlobalId != relation.GlobalId)
                .Where(node => node.GlobalId != endpoint.MemberTypeId)
                .Where(node => !IsDirectChildOf(node.GlobalId, endpointNode.GlobalId))
                .DistinctBy(static node => node.GlobalId)
                .ToArray();
            if (!endpoint.Cardinality.Contains(participantStates.Length))
                throw new InvalidOperationException(
                    $"Endpoint '{endpoint.Name}' of typed edge '{relation.GlobalId}' expects {endpoint.Cardinality} participants, but found {participantStates.Length}.");

            endpoints.Add(new TypedEdgeEndpointInstance(
                endpoint,
                new Node(endpointNode),
                participantStates.Select(static node => new Node(node)).ToArray()));
        }

        return new TypedEdgeInstance(new Node(relation), definition, endpoints);
    }

    public static async Task<IReadOnlyCollection<NodeBacking>> FindIncidentRelationsAsync(
        NodeBacking participant,
        TypedEdgeDefinition definition,
        TypedEdgeEndpointDefinition endpoint)
    {
        var relations = new Dictionary<InternalId, NodeBacking>();
        await foreach (var endpointNode in participant.Nodes.ConfigureAwait(false)) {
            if (!await HasMemberClassifierAsync(endpointNode, endpoint.MemberTypeId).ConfigureAwait(false))
                continue;

            await foreach (var relation in endpointNode.Nodes.ConfigureAwait(false)) {
                if (!IsDirectChildOf(endpointNode.GlobalId, relation.GlobalId))
                    continue;
                if (await HasClassifierAsync(relation, definition.Type.GlobalId).ConfigureAwait(false))
                    relations[relation.GlobalId] = relation;
            }
        }

        return relations.Values.ToArray();
    }

    public static async Task<bool> HasClassifierAsync(NodeBacking relation, InternalId typeId)
    {
        return await relation.Nodes
            .AnyAsync(node => node.GlobalId == typeId)
            .ConfigureAwait(false);
    }

    public static async Task<bool> HasMemberClassifierAsync(NodeBacking endpoint, InternalId memberTypeId)
    {
        return await endpoint.Nodes
            .AnyAsync(node => node.GlobalId == memberTypeId)
            .ConfigureAwait(false);
    }

    public static bool HasMemberClassifier(Node endpoint, InternalId memberTypeId) =>
        endpoint.Nodes.Any(node => node.GlobalId == memberTypeId);

    public static InternalId MemberTypeId(NodeType relationType, string memberName) {
        return TypedEdgeDefinition.MemberTypeId(relationType, memberName);
    }

    public static bool IsDirectChildOf(InternalId nodeId, InternalId parentId)
    {
        var nodeSegments = nodeId.ToArray();
        var parentSegments = parentId.ToArray();
        return nodeSegments.Length == parentSegments.Length + 1
            && parentSegments.SequenceEqual(nodeSegments.Take(parentSegments.Length));
    }

    private static async Task<NodeBacking> EnsureMemberTypeAsync(
        IGraphStorage storage,
        NodeType relationType,
        TypedEdgeEndpointDefinition endpoint)
    {
        var definition = await storage.Get(relationType.GlobalId, DefinitionLocalId).ConfigureAwait(false)
            ?? await storage.Create(DefinitionLocalId, relationType.GlobalId).ConfigureAwait(false);
        var fields = await storage.Get(definition.GlobalId, FieldsLocalId).ConfigureAwait(false)
            ?? await storage.Create(FieldsLocalId, definition.GlobalId).ConfigureAwait(false);
        var fieldDefinition = await storage.Get(fields.GlobalId, new NodeLocalId(endpoint.Name)).ConfigureAwait(false)
            ?? await storage.Create(new NodeLocalId(endpoint.Name), fields.GlobalId).ConfigureAwait(false);
        var memberTypeLocalId = endpoint.MemberTypeId.Last();
        var memberType = await storage.Get(fieldDefinition.GlobalId, memberTypeLocalId).ConfigureAwait(false)
            ?? await storage.Create(memberTypeLocalId, fieldDefinition.GlobalId).ConfigureAwait(false);
        if (memberType.GlobalId != endpoint.MemberTypeId)
            throw new InvalidOperationException(
                $"Member type '{endpoint.Name}' of relation type '{relationType.GlobalId}' has unexpected raw identity '{memberType.GlobalId}'.");
        return memberType;
    }

    private static NodeLocalId EndpointOccurrenceLocalId(NodeLocalId relationLocalId, int index) {
        return new NodeLocalId($"member-{relationLocalId}-{index + 1}");
    }
}
