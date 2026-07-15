using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

/// <summary>
/// Maps typed edge domain objects to canonical semantic relation records.
/// The simple nodes exposed by <see cref="IGraphStorage"/> are used only as a
/// compatibility projection for existing Domain models and callers.
/// </summary>
internal static class TypedEdgeSubgraphCodec
{
    public static async Task<NodeBacking> CreateAsync(
        IGraphStorage storage,
        NodeLocalId relationLocalId,
        TypedEdgeDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyCollection<NodeBacking>> participants)
    {
        var semanticStorage = RequireSemanticStorage(storage);
        var members = new List<SemanticRelationMember>(definition.Endpoints.Count);

        foreach (var endpoint in definition.Endpoints) {
            if (!participants.TryGetValue(endpoint.Name, out var endpointParticipants))
                throw new InvalidOperationException($"Endpoint '{endpoint.Name}' is missing.");

            var participantIds = endpointParticipants
                .DistinctBy(static participant => participant.GlobalId)
                .Select(static participant => participant.GlobalId)
                .ToArray();
            if (!endpoint.Cardinality.Contains(participantIds.Length))
                throw new InvalidOperationException(
                    $"Endpoint '{endpoint.Name}' expects {endpoint.Cardinality} participants, but got {participantIds.Length}.");

            members.Add(new SemanticRelationMember(
                endpoint.Name,
                endpoint.MemberTypeId,
                participantIds));
        }

        return await semanticStorage.CreateRelationAsync(
            relationLocalId,
            definition.Type.GlobalId,
            members).ConfigureAwait(false);
    }

    public static async Task<TypedEdgeInstance> ReadAsync(
        Graph graph,
        NodeBacking relation,
        TypedEdgeDefinition definition)
    {
        var compatibilityStorage = graph.Storage;
        var semanticStorage = RequireSemanticStorage(compatibilityStorage);
        var persisted = await semanticStorage.GetRelationAsync(relation.GlobalId).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Semantic relation '{relation.GlobalId}' was not found.");

        if (!persisted.TypeIds.Contains(definition.Type.GlobalId))
            throw new InvalidOperationException(
                $"Relation '{relation.GlobalId}' is not classified by typed edge type '{definition.Type.GlobalId}'.");

        var endpoints = new List<TypedEdgeEndpointInstance>(definition.Endpoints.Count);
        var endpointIndex = 0;
        foreach (var endpoint in definition.Endpoints) {
            var matchingMembers = persisted.Members
                .Where(member => string.Equals(member.Name, endpoint.Name, StringComparison.Ordinal))
                .Where(member => member.MemberTypeId == endpoint.MemberTypeId)
                .ToArray();
            if (matchingMembers.Length != 1)
                throw new InvalidOperationException(
                    $"Typed edge '{relation.GlobalId}' must contain exactly one semantic member '{endpoint.Name}' "
                    + $"with member type '{endpoint.MemberTypeId}', but found {matchingMembers.Length}.");

            var member = matchingMembers[0];
            var participantIds = member.ParticipantIds
                .Distinct()
                .ToArray();
            if (!endpoint.Cardinality.Contains(participantIds.Length))
                throw new InvalidOperationException(
                    $"Endpoint '{endpoint.Name}' of typed edge '{relation.GlobalId}' expects {endpoint.Cardinality} participants, but found {participantIds.Length}.");

            var participantStates = new List<NodeBacking>(participantIds.Length);
            foreach (var participantId in participantIds) {
                var participant = await compatibilityStorage.Get(participantId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Participant '{participantId}' of semantic relation '{relation.GlobalId}' was not found in the compatibility projection.");
                participantStates.Add(participant);
            }

            var endpointLocalId = EndpointOccurrenceLocalId(relation.LocalId, endpointIndex++);
            var endpointNode = await compatibilityStorage
                .Get(relation.GlobalId, endpointLocalId)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Semantic member projection '{relation.GlobalId}/{endpointLocalId}' was not found.");

            endpoints.Add(new TypedEdgeEndpointInstance(
                endpoint,
                new Node(endpointNode),
                participantStates.Select(static participant => new Node(participant)).ToArray()));
        }

        return new TypedEdgeInstance(new Node(relation), definition, endpoints);
    }

    /// <summary>
    /// Finds relations through the canonical participant incidence index. The
    /// compatibility storage is needed only to materialize legacy NodeBacking
    /// wrappers for the returned semantic relation ids.
    /// </summary>
    public static async Task<IReadOnlyCollection<NodeBacking>> FindIncidentRelationsAsync(
        IGraphStorage compatibilityStorage,
        NodeBacking participant,
        TypedEdgeDefinition definition,
        TypedEdgeEndpointDefinition endpoint)
    {
        var semanticStorage = RequireSemanticStorage(compatibilityStorage);
        var relations = new Dictionary<InternalId, NodeBacking>();
        await foreach (var candidate in semanticStorage.FindIncidentRelationsAsync(
                           participant.GlobalId,
                           definition.Type.GlobalId,
                           endpoint.Name).ConfigureAwait(false)) {
            if (!candidate.TypeIds.Contains(definition.Type.GlobalId))
                continue;

            var matchingMembers = candidate.Members
                .Where(member => string.Equals(member.Name, endpoint.Name, StringComparison.Ordinal))
                .Where(member => member.MemberTypeId == endpoint.MemberTypeId)
                .Where(member => member.ParticipantIds.Contains(participant.GlobalId))
                .ToArray();
            if (matchingMembers.Length == 0)
                continue;
            if (matchingMembers.Length > 1)
                throw new InvalidOperationException(
                    $"Semantic relation '{candidate.Id}' contains member '{endpoint.Name}' more than once.");

            var relation = await compatibilityStorage.Get(candidate.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Semantic relation '{candidate.Id}' is missing from the compatibility projection.");
            relations[relation.GlobalId] = relation;
        }

        return relations.Values.ToArray();
    }

    /// <summary>
    /// Kept temporarily so callers fail explicitly instead of silently falling
    /// back to compatibility-topology traversal. Callers must pass their
    /// compatibility storage to use the semantic incidence index.
    /// </summary>
    [Obsolete("Pass IGraphStorage as the first argument so the semantic incidence index can be used.")]
    public static Task<IReadOnlyCollection<NodeBacking>> FindIncidentRelationsAsync(
        NodeBacking participant,
        TypedEdgeDefinition definition,
        TypedEdgeEndpointDefinition endpoint)
    {
        throw new InvalidOperationException(
            "Semantic incidence lookup requires the compatibility storage. "
            + "Use FindIncidentRelationsAsync(storage, participant, definition, endpoint).");
    }

    public static Task<bool> HasClassifierAsync(NodeBacking relation, InternalId typeId)
    {
        return Task.FromResult(
            relation is ISemanticNodeBacking semantic
            && semantic.DeclaredTypeIds.Contains(typeId));
    }

    public static Task<bool> HasMemberClassifierAsync(NodeBacking endpoint, InternalId memberTypeId)
    {
        return Task.FromResult(
            endpoint is ISemanticNodeBacking semantic
            && semantic.RelationMembers.Any(member => member.MemberTypeId == memberTypeId));
    }

    public static bool HasMemberClassifier(Node endpoint, InternalId memberTypeId) =>
        endpoint.Backing is ISemanticNodeBacking semantic
        && semantic.RelationMembers.Any(member => member.MemberTypeId == memberTypeId);

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

    private static ISemanticGraphStorage RequireSemanticStorage(IGraphStorage storage)
    {
        return storage as ISemanticGraphStorage
            ?? throw new InvalidOperationException(
                $"Graph storage '{storage.GetType().FullName}' does not implement {nameof(ISemanticGraphStorage)}.");
    }

    private static NodeLocalId EndpointOccurrenceLocalId(NodeLocalId relationLocalId, int index) {
        return new NodeLocalId($"@member-{relationLocalId}-{index + 1}");
    }
}
