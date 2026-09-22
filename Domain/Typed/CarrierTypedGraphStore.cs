using System.Collections.ObjectModel;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Typed;

namespace GraphData.Core.Services;

/// <summary>Projects the existing carrier grammar without exposing its active nodes.</summary>
public sealed class CarrierTypedGraphStore : ITypedGraphStore
{
    readonly Graph graph;
    readonly GraphService service;

    public CarrierTypedGraphStore(Graph graph)
    {
        this.graph = graph;
        service = new GraphService(graph, new GraphSearchService(graph.Storage));
    }

    public async Task<TypedType?> ReadTypeAsync(string id)
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var state = await ReadBackingAsync(id).ConfigureAwait(false);
        if (state is null)
            return null;
        var type = await graph.AsNodeTypeAsync(state).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.Invalid, $"'{id}' is not a type in the selected catalog.");
        return MapType(ReadDefinition(type));
    }

    public async Task<IReadOnlyList<TypedType>> ReadTypesAsync()
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var definitions = Require(await service.GetTypeDefinitionsAsync().ConfigureAwait(false));
        return Array.AsReadOnly(definitions
            .Where(definition => !IsInfrastructureType(definition.Type.GlobalId))
            .Select(definition => MapType(ReadDefinition(definition.Type)))
            .OrderBy(static type => type.Id, StringComparer.Ordinal)
            .ToArray());
    }

    public async Task<TypedElement?> ReadElementAsync(string id)
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var state = await ReadBackingAsync(id).ConfigureAwait(false);
        if (state is null)
            return null;
        await EnsureElementAddressAsync(state).ConfigureAwait(false);
        var classifiers = await ReadEdgeClassifiersAsync(state).ConfigureAwait(false);
        if (classifiers.Count > 0) {
            if (classifiers.Any(definition => IsInfrastructureType(definition.Type.GlobalId)))
                throw Error(TypedGraphError.Unsupported, $"'{id}' is carrier infrastructure.");
            if (classifiers.Count != 1)
                throw Error(TypedGraphError.Corrupt, $"Relation '{id}' has multiple edge classifiers.");
            var definition = classifiers[0];
            _ = MapType(definition);
            var relation = await ReadRelationAsync(state, definition).ConfigureAwait(false);
            await ValidateRelationShapeAsync(relation, allowAttributes: true).ConfigureAwait(false);
            return Snapshot(relation);
        }
        await foreach (var child in state.Nodes.ConfigureAwait(false)) {
            if (TypedEdgeSubgraphCodec.IsDirectChildOf(child.GlobalId, state.GlobalId)
                && await HasRoleClassifierAsync(child).ConfigureAwait(false))
                throw Error(TypedGraphError.Corrupt, $"Carrier '{id}' has typed relation roles but no edge classifier.");
        }

        var instance = Require(await service.GetSemanticNodeAsync(state.GlobalId).ConfigureAwait(false), TypedGraphError.Corrupt);
        foreach (var type in instance.AssignedTypes) {
            var definition = MapType(ReadDefinition(type));
            if (definition.Kind != TypedElementKind.Instance)
                throw Error(TypedGraphError.Corrupt, $"Instance '{id}' has relation type '{definition.Id}'.");
        }
        return new TypedElement(id, TypedElementKind.Instance,
            Array.AsReadOnly(instance.AssignedTypes.Select(static type => type.GlobalId.ToString()).Order(StringComparer.Ordinal).ToArray()),
            CopyAttributes(instance.Attributes), EmptyMembers());
    }

    public async Task<IReadOnlyList<TypedElement>> ReadIncidentRelationsAsync(string participantId)
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var participant = await ReadBackingAsync(participantId).ConfigureAwait(false);
        if (participant is null)
            return Array.Empty<TypedElement>();
        await EnsureElementAddressAsync(participant).ConfigureAwait(false);
        var candidates = new Dictionary<string, CarrierNodeBacking>(StringComparer.Ordinal);
        await foreach (var neighbor in participant.Nodes.ConfigureAwait(false)) {
            await AddCandidateAsync(neighbor, candidates).ConfigureAwait(false);
            var parent = await ReadParentAsync(neighbor.GlobalId).ConfigureAwait(false);
            if (parent is not null)
                await AddCandidateAsync(parent, candidates).ConfigureAwait(false);
        }
        var result = new List<TypedElement>();
        foreach (var id in candidates.Keys.Order(StringComparer.Ordinal)) {
            var relation = await ReadElementAsync(id).ConfigureAwait(false)
                ?? throw Error(TypedGraphError.Corrupt, $"Incident relation '{id}' disappeared.");
            if (relation.Members.Values.Any(ids => ids.Contains(participantId, StringComparer.Ordinal)))
                result.Add(relation);
        }
        return result.AsReadOnly();
    }

    public async Task InsertTypeAsync(TypedType definition)
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var prefix = graph.NodeTypes.GlobalId + "/";
        if (!definition.Id.StartsWith(prefix, StringComparison.Ordinal))
            throw Error(TypedGraphError.Unsupported, "Types must be created in the selected NodeTypes catalog.");
        var localId = RequireLocalId(definition.Id[prefix.Length..]);
        await EnsureAbsentAsync(definition.Id).ConfigureAwait(false);
        EnsureCaseUnique(definition.Members.Select(static member => member.Name).Concat(definition.Attributes.Select(static attribute => attribute.Name)));
        if (definition.Kind == TypedElementKind.Instance && definition.Members.Count != 0)
            throw Error(TypedGraphError.Unsupported, "Named instance fields are not represented by this carrier adapter.");
        if (definition.Kind == TypedElementKind.Relation && definition.RequiredTypeIds.Count != 0)
            throw Error(TypedGraphError.Unsupported, "Relation type requirements need an explicit role-composition contract.");
        var fields = new List<NodeFieldDefinition>();
        foreach (var attribute in definition.Attributes)
            fields.Add(new NodeFieldDefinition(attribute.Name, NodeFieldValueKind.Primitive,
                ToClrType(attribute.Kind), attribute.Required ? NodeSlotCardinality.Required() : NodeSlotCardinality.Optional(), false));
        foreach (var member in definition.Members) {
            var type = member.TypeId is null ? null : await RequireTypeAsync(member.TypeId).ConfigureAwait(false);
            fields.Add(new NodeFieldDefinition(member.Name, NodeFieldValueKind.Node, typeof(Node),
                new NodeSlotCardinality(member.Min, member.Max), member.Max is null or > 1, type));
        }
        var required = new List<NodeType>();
        foreach (var typeId in definition.RequiredTypeIds)
            required.Add(await RequireTypeAsync(typeId).ConfigureAwait(false));
        var result = definition.Kind switch {
            TypedElementKind.Instance => await service.CreateNodeType(localId, definition.IsAbstract, fields, requiredTypes: required).ConfigureAwait(false),
            TypedElementKind.Relation => await service.CreateEdgeType(localId, definition.IsAbstract, fields, requiredTypes: required).ConfigureAwait(false),
            _ => throw Error(TypedGraphError.Unsupported, $"Unknown element kind '{definition.Kind}'.")
        };
        _ = Require(result);
    }

    public async Task InsertElementAsync(TypedElement element)
    {
        await graph.OpenAsync().ConfigureAwait(false);
        var localId = RequireLocalId(element.Id);
        await EnsureAbsentAsync(element.Id).ConfigureAwait(false);
        EnsureCaseUnique(element.Attributes.Keys);
        var definitions = new List<NodeTypeDefinition>();
        foreach (var id in element.TypeIds) {
            var type = await RequireTypeAsync(id).ConfigureAwait(false);
            var definition = ReadDefinition(type);
            var mapped = MapType(definition);
            if (mapped.Kind != element.Kind)
                throw Error(TypedGraphError.Invalid, $"Type '{id}' cannot classify '{element.Kind}'.");
            definitions.Add(definition);
        }

        if (element.Kind == TypedElementKind.Relation) {
            if (definitions.Count != 1)
                throw Error(TypedGraphError.Unsupported, "A named relation requires exactly one edge type.");
            var definition = TypedEdgeDefinition.Create(definitions[0]);
            var participants = new Dictionary<string, IReadOnlyCollection<CarrierNodeBacking>>(StringComparer.Ordinal);
            if (!element.Members.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(definition.Endpoints.Select(static endpoint => endpoint.Name)))
                throw Error(TypedGraphError.Invalid, "Relation roles do not match its type.");
            foreach (var endpoint in definition.Endpoints) {
                var ids = element.Members[endpoint.Name];
                if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || !endpoint.Cardinality.Contains(ids.Count))
                    throw Error(TypedGraphError.Invalid, $"Invalid participants for role '{endpoint.Name}'.");
                var states = new List<CarrierNodeBacking>();
                foreach (var id in ids) {
                    var snapshot = await ReadElementAsync(id).ConfigureAwait(false)
                        ?? throw Error(TypedGraphError.NotFound, $"Participant '{id}' was not found.");
                    if (endpoint.NodeType is not null && !snapshot.TypeIds.Contains(endpoint.NodeType.GlobalId.ToString(), StringComparer.Ordinal))
                        throw Error(TypedGraphError.Invalid, $"Participant '{id}' does not satisfy role '{endpoint.Name}'.");
                    states.Add(await ReadBackingAsync(id).ConfigureAwait(false)
                        ?? throw Error(TypedGraphError.NotFound, $"Participant '{id}' was not found."));
                }
                participants.Add(endpoint.Name, states);
            }
            await ValidateRelationNamespacesAsync(localId, definition, participants).ConfigureAwait(false);
            var created = await TypedEdgeSubgraphCodec.CreateAsync(graph.Storage, localId, definition, participants).ConfigureAwait(false);
            try {
                await graph.Storage.Update(created.GlobalId, element.Attributes.ToDictionary()).ConfigureAwait(false);
            } catch {
                await graph.Storage.Delete(created).ConfigureAwait(false);
                throw;
            }
            return;
        }

        if (element.Kind != TypedElementKind.Instance || element.Members.Count != 0)
            throw Error(TypedGraphError.Unsupported, "Named instance fields are not represented by this carrier adapter.");
        // Abstract required facets are materialized by assigning their concrete dependent.
        var assignments = definitions.Where(static definition => !definition.IsAbstract).ToArray();
        if (assignments.Length == 0 && definitions.Count > 0)
            throw Error(TypedGraphError.Invalid, "An abstract type cannot be assigned directly.");
        var typeIds = definitions.Select(static definition => definition.Type.GlobalId.ToString()).ToArray();
        await ValidateNamespaceSlotsAsync(element.Id, typeIds).ConfigureAwait(false);
        foreach (var typeId in typeIds)
            await ValidateNamespaceSlotsAsync(typeId, [element.Id]).ConfigureAwait(false);
        _ = Require(await service.CreateNode(localId, attributes: element.Attributes.ToDictionary()).ConfigureAwait(false));
        try {
            foreach (var definition in assignments)
                _ = Require(await service.AssignNodeTypeAsync(ParseId(element.Id), definition.Type.GlobalId).ConfigureAwait(false));
            var stored = await ReadElementAsync(element.Id).ConfigureAwait(false)
                ?? throw Error(TypedGraphError.Corrupt, $"Created instance '{element.Id}' disappeared.");
            if (!stored.TypeIds.ToHashSet(StringComparer.Ordinal).SetEquals(element.TypeIds))
                throw Error(TypedGraphError.Invalid, "Instance type ids must include exactly its materialized required facets.");
        } catch {
            Require(await service.DeleteNode(ParseId(element.Id)).ConfigureAwait(false));
            throw;
        }
    }

    public async Task ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes)
    {
        EnsureCaseUnique(attributes.Keys);
        _ = await ReadElementAsync(id).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Element '{id}' was not found.");
        Require(await service.UpdateNode(ParseId(id), attributes.ToDictionary()).ConfigureAwait(false));
    }

    public async Task DeleteElementAsync(string id)
    {
        var snapshot = await ReadElementAsync(id).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Element '{id}' was not found.");
        if ((await ReadIncidentRelationsAsync(id).ConfigureAwait(false)).Count > 0)
            throw Error(TypedGraphError.Conflict, $"Element '{id}' is referenced by a relation.");
        var state = await ReadBackingAsync(id).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Element '{id}' was not found.");
        var witnesses = new List<TypedEdgeInstance>();
        if (snapshot.Kind == TypedElementKind.Instance) {
            var instance = Require(await service.GetSemanticNodeAsync(state.GlobalId).ConfigureAwait(false), TypedGraphError.Corrupt);
            var allowed = instance.AssignedTypes.Select(static type => type.GlobalId.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var facet in instance.TypeInstances) {
                if (facet.Witness is null)
                    continue;
                var witness = await ReadRelationAsync(facet.Witness.Backing,
                    ReadDefinition(graph.GetRequiredRuntimeType<InstanceOfEdge>())).ConfigureAwait(false);
                await ValidateRelationShapeAsync(witness, allowAttributes: false, restrictExternalConnections: true).ConfigureAwait(false);
                if (witness.Endpoint(nameof(InstanceOfEdge.Instance)).Participant.GlobalId != state.GlobalId
                    || witness.Endpoint(nameof(InstanceOfEdge.Type)).Participant.GlobalId != facet.Type.GlobalId)
                    throw Error(TypedGraphError.Corrupt, $"Invalid facet witness '{witness.Relation.GlobalId}'.");
                allowed.Add(witness.Endpoint(nameof(InstanceOfEdge.Instance)).EndpointNode.GlobalId.ToString());
                witnesses.Add(witness);
            }
            await EnsureOnlyNeighborsAsync(state, allowed).ConfigureAwait(false);
        } else {
            var type = await RequireTypeAsync(snapshot.TypeIds.Single()).ConfigureAwait(false);
            var relation = await ReadRelationAsync(state, ReadDefinition(type)).ConfigureAwait(false);
            await ValidateRelationShapeAsync(relation, allowAttributes: true, restrictExternalConnections: true).ConfigureAwait(false);
        }
        // All destructive checks complete before deleting even the first witness.
        foreach (var witness in witnesses)
            await graph.Storage.Delete(witness.Relation.Backing).ConfigureAwait(false);
        await graph.Storage.Delete(state).ConfigureAwait(false);
    }

    NodeTypeDefinition ReadDefinition(NodeType type)
    {
        try {
            var effective = graph.GetNodeTypeDefinition(type);
            var persisted = DynamicNodeTypeDefinitionStorage.TryRead(type);
            if (persisted is null) {
                if (effective.Fields.Count > 0 || effective.Slots.Count > 0 || effective.IsAbstract
                    || effective.ElementKind != GraphElementKind.Node)
                    throw Error(TypedGraphError.Corrupt,
                        $"Type '{type.GlobalId}' has no persisted definition for its nontrivial cached or CLR schema.");
                return effective;
            }
            if (effective.ElementKind != persisted.ElementKind || effective.IsAbstract != persisted.IsAbstract
                || !FieldSignatures(effective).SequenceEqual(FieldSignatures(persisted))
                || !SlotSignatures(effective).SetEquals(SlotSignatures(persisted)))
                throw Error(TypedGraphError.Corrupt,
                    $"Type '{type.GlobalId}' has conflicting cached or CLR and persisted schemas; resolve the schema conflict explicitly.");
            // TryRead covers the Definition subtree only. Requires are separate carrier
            // relations merged by Graph, not a second list in that subtree.
            return effective;
        } catch (InvalidOperationException error) when (error is not TypedGraphException) {
            throw Error(TypedGraphError.Corrupt, $"Cannot read type '{type.GlobalId}': {error.Message}");
        }
    }

    static IEnumerable<(string Name, NodeFieldValueKind Kind, Type ClrType, NodeSlotCardinality Cardinality, bool IsCollection, string? TypeId)>
        FieldSignatures(NodeTypeDefinition definition) => definition.Fields
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .Select(static field => (field.Name, field.ValueKind, field.ClrType, field.Cardinality, field.IsCollection, field.NodeType?.GlobalId.ToString()));

    static HashSet<string> SlotSignatures(NodeTypeDefinition definition) => definition.Slots
        // Persisted schema intentionally deduplicates identical derived/explicit slots.
        .Select(static slot => System.Text.Json.JsonSerializer.Serialize(new {
            slot.Name,
            slot.Cardinality,
            AllowedTypeIds = slot.AllowedTypes.Select(static type => type.GlobalId.ToString()).Order(StringComparer.Ordinal).ToArray()
        }))
        .ToHashSet(StringComparer.Ordinal);

    TypedType MapType(NodeTypeDefinition definition)
    {
        if (IsInfrastructureType(definition.Type.GlobalId))
            throw Error(TypedGraphError.Unsupported, $"Type '{definition.Type.GlobalId}' is carrier infrastructure.");
        var isRelation = definition.ElementKind == GraphElementKind.Edge;
        if (definition.ElementKind is not (GraphElementKind.Node or GraphElementKind.Edge))
            throw Error(TypedGraphError.Unsupported, $"Unknown type kind '{definition.ElementKind}'.");
        var nodeFields = definition.Fields.Where(static field => field.ValueKind == NodeFieldValueKind.Node).ToArray();
        if (!isRelation && nodeFields.Length > 0)
            throw Error(TypedGraphError.Unsupported, $"Type '{definition.Type.GlobalId}' has unsupported named instance fields.");
        foreach (var slot in definition.Slots) {
            if (!nodeFields.Any(field => field.Name == slot.Name && field.NodeType is not null
                && slot.AllowedTypes.Count == 1 && slot.AllowedTypes.Single().GlobalId == field.NodeType.GlobalId
                && slot.Cardinality == field.Cardinality))
                throw Error(TypedGraphError.Unsupported, $"Type '{definition.Type.GlobalId}' has an independent slot '{slot.Name}'.");
        }
        if (isRelation && definition.RequiredTypes.Count > 0)
            throw Error(TypedGraphError.Unsupported, "Relation type requirements need an explicit role-composition contract.");
        if (isRelation && !TypedEdgeDefinition.TryCreate(definition, out _))
            throw Error(TypedGraphError.Corrupt, $"Relation type '{definition.Type.GlobalId}' has no valid named role schema.");
        var attributes = new List<TypedAttributeDefinition>();
        foreach (var field in definition.Fields.Where(static field => field.ValueKind != NodeFieldValueKind.Node)) {
            if (field.ValueKind != NodeFieldValueKind.Primitive || field.IsCollection || field.Cardinality.Max != 1 || field.Cardinality.Min is < 0 or > 1)
                throw Error(TypedGraphError.Unsupported, $"Field '{field.Name}' is not a supported scalar attribute.");
            attributes.Add(new TypedAttributeDefinition(field.Name, ToScalarKind(field.ClrType), field.Cardinality.Min == 1));
        }
        return new TypedType(definition.Type.GlobalId.ToString(), isRelation ? TypedElementKind.Relation : TypedElementKind.Instance,
            definition.IsAbstract,
            Array.AsReadOnly(definition.RequiredTypes.Select(static type => type.GlobalId.ToString()).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(nodeFields.Select(static field => new TypedMemberDefinition(field.Name, field.NodeType?.GlobalId.ToString(), field.Cardinality.Min, field.Cardinality.Max)).ToArray()),
            attributes.AsReadOnly());
    }

    async Task<NodeType> RequireTypeAsync(string id)
    {
        _ = await ReadTypeAsync(id).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Type '{id}' was not found.");
        return await service.GetTypeNode(ParseId(id)).ConfigureAwait(false)
            ?? throw Error(TypedGraphError.NotFound, $"Type '{id}' was not found.");
    }

    async Task<IReadOnlyList<NodeTypeDefinition>> ReadEdgeClassifiersAsync(CarrierNodeBacking state)
    {
        var result = new Dictionary<string, NodeTypeDefinition>(StringComparer.Ordinal);
        await foreach (var neighbor in state.Nodes.ConfigureAwait(false)) {
            var type = await graph.AsNodeTypeAsync(neighbor).ConfigureAwait(false);
            if (type is null)
                continue;
            var definition = ReadDefinition(type);
            if (definition.ElementKind == GraphElementKind.Edge)
                result[neighbor.GlobalId.ToString()] = definition;
        }
        return result.Values.ToArray();
    }

    async Task AddCandidateAsync(CarrierNodeBacking candidate, Dictionary<string, CarrierNodeBacking> candidates)
    {
        var id = candidate.GlobalId.ToString();
        var catalog = graph.NodeTypes.GlobalId.ToString();
        if (id.Length == 0 || id == catalog || id.StartsWith(catalog + "/", StringComparison.Ordinal)
            || await graph.IsNodeTypeAsync(candidate).ConfigureAwait(false))
            return;
        var classifiers = await ReadEdgeClassifiersAsync(candidate).ConfigureAwait(false);
        if (classifiers.Count == 0) {
            await foreach (var child in candidate.Nodes.ConfigureAwait(false))
                if (TypedEdgeSubgraphCodec.IsDirectChildOf(child.GlobalId, candidate.GlobalId)
                    && await HasRoleClassifierAsync(child).ConfigureAwait(false))
                    throw Error(TypedGraphError.Corrupt, $"Incident carrier '{id}' has roles but no edge classifier.");
            return;
        }
        if (classifiers.All(definition => IsInfrastructureType(definition.Type.GlobalId)))
            return;
        candidates[id] = candidate;
    }

    async Task<TypedEdgeInstance> ReadRelationAsync(CarrierNodeBacking state, NodeTypeDefinition definition)
    {
        try {
            return await TypedEdgeSubgraphCodec.ReadAsync(graph, state,
                TypedEdgeDefinition.Create(definition)).ConfigureAwait(false);
        } catch (InvalidOperationException error) when (error is not TypedGraphException) {
            throw Error(TypedGraphError.Corrupt, $"Malformed relation '{state.GlobalId}': {error.Message}");
        }
    }

    async Task ValidateRelationShapeAsync(TypedEdgeInstance relation, bool allowAttributes, bool restrictExternalConnections = false)
    {
        if (!allowAttributes && relation.Relation.Attributes.Count > 0)
            throw Error(TypedGraphError.Unsupported, $"Facet witness '{relation.Relation.GlobalId}' contains unrepresented attributes.");
        var allowed = relation.Endpoints.Select(static endpoint => endpoint.EndpointNode.GlobalId.ToString()).ToHashSet(StringComparer.Ordinal);
        allowed.Add(relation.Type.GlobalId.ToString());
        await foreach (var neighbor in relation.Relation.Backing.Nodes.ConfigureAwait(false)) {
            if (!allowed.Contains(neighbor.GlobalId.ToString())
                && (restrictExternalConnections || TypedEdgeSubgraphCodec.IsDirectChildOf(neighbor.GlobalId, relation.Relation.GlobalId)))
                throw Error(TypedGraphError.Unsupported, $"Relation '{relation.Relation.GlobalId}' has an unrepresented connection or child '{neighbor.GlobalId}'.");
        }
        foreach (var endpoint in relation.Endpoints) {
            if (endpoint.EndpointNode.Attributes.Count > 0)
                throw Error(TypedGraphError.Unsupported, $"Role carrier '{endpoint.EndpointNode.GlobalId}' contains unrepresented attributes.");
            var endpointNeighbors = endpoint.Participants.Select(static node => node.GlobalId.ToString()).ToHashSet(StringComparer.Ordinal);
            endpointNeighbors.Add(relation.Relation.GlobalId.ToString());
            endpointNeighbors.Add(endpoint.Definition.MemberTypeId.ToString());
            await EnsureOnlyNeighborsAsync(endpoint.EndpointNode.Backing, endpointNeighbors).ConfigureAwait(false);
        }
    }

    static async Task EnsureOnlyNeighborsAsync(CarrierNodeBacking state, IReadOnlySet<string> allowed)
    {
        await foreach (var neighbor in state.Nodes.ConfigureAwait(false))
            if (!allowed.Contains(neighbor.GlobalId.ToString()))
                throw Error(TypedGraphError.Unsupported, $"Carrier '{state.GlobalId}' has an unrepresented connection or child '{neighbor.GlobalId}'.");
    }

    async Task EnsureElementAddressAsync(CarrierNodeBacking state)
    {
        var id = state.GlobalId.ToString();
        var catalog = graph.NodeTypes.GlobalId.ToString();
        if (id.Length == 0 || id == catalog || id.StartsWith(catalog + "/", StringComparison.Ordinal)
            || await graph.IsNodeTypeAsync(state).ConfigureAwait(false)
            || await HasRoleClassifierAsync(state).ConfigureAwait(false))
            throw Error(TypedGraphError.Unsupported, $"'{id}' belongs to the type catalog or carrier infrastructure.");
        var parent = await ReadParentAsync(state.GlobalId).ConfigureAwait(false);
        if (parent is not null && (await ReadEdgeClassifiersAsync(parent).ConfigureAwait(false)).Count > 0)
            throw Error(TypedGraphError.Unsupported, $"'{id}' is a child of a relation carrier.");
    }

    async Task<bool> HasRoleClassifierAsync(CarrierNodeBacking state)
    {
        await foreach (var neighbor in state.Nodes.ConfigureAwait(false)) {
            // Candidate discovery follows the existing codec's member-type address;
            // identity is then checked against its actual schema, not a name prefix.
            var parts = neighbor.GlobalId.Select(static segment => segment.ToString()).ToArray();
            if (parts.Length < 5 || parts[^4] != "Definition" || parts[^3] != "Fields")
                continue;
            var type = await service.GetTypeNode(new NodePath(parts[..^4])).ConfigureAwait(false);
            if (type is not null && TypedEdgeDefinition.TryCreate(ReadDefinition(type), out var definition)
                && definition.Endpoints.Any(endpoint => endpoint.MemberTypeId == neighbor.GlobalId))
                return true;
        }
        return false;
    }

    async Task<CarrierNodeBacking?> ReadParentAsync(InternalId id)
    {
        var segments = id.Select(static segment => segment.ToString()).ToArray();
        return segments.Length <= 1 ? null : await graph.Storage.Get(new NodePath(segments[..^1])).ConfigureAwait(false);
    }

    async Task<CarrierNodeBacking?> ReadBackingAsync(string id)
    {
        var state = await graph.Storage.Get(ParseId(id)).ConfigureAwait(false);
        if (state is not null && state.GlobalId.ToString() != id)
            throw Error(TypedGraphError.Unsupported, $"'{id}' is an alias; use canonical identity '{state.GlobalId}'.");
        return state;
    }

    async Task EnsureAbsentAsync(string id)
    {
        if (await graph.Storage.Get(ParseId(id)).ConfigureAwait(false) is not null)
            throw Error(TypedGraphError.Conflict, $"Carrier '{id}' already exists.");
    }

    async Task ValidateRelationNamespacesAsync(
        NodeLocalId relationLocalId,
        TypedEdgeDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyCollection<CarrierNodeBacking>> participants)
    {
        // A carrier namespace combines children and links, indexed by the neighbour's local name.
        // Validate both directions before the codec can create the relation or a lazy member type.
        var planned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var relationId = relationLocalId.ToString();
        PlanLink(relationId, definition.Type.GlobalId.ToString());
        var endpointIndex = 0;
        foreach (var endpoint in definition.Endpoints) {
            var endpointId = $"{relationId}/member-{relationId}-{++endpointIndex}";
            var memberTypeId = endpoint.MemberTypeId.ToString();
            PlanLink(relationId, endpointId);
            PlanLink(endpointId, memberTypeId);
            // A newly materialized member type also has its field-definition parent as a neighbour.
            PlanLink(memberTypeId, memberTypeId[..memberTypeId.LastIndexOf('/')]);
            foreach (var participant in participants[endpoint.Name])
                PlanLink(endpointId, participant.GlobalId.ToString());
        }

        foreach (var (ownerId, neighbours) in planned)
            await ValidateNamespaceSlotsAsync(ownerId, neighbours).ConfigureAwait(false);

        void PlanLink(string first, string second)
        {
            if (!planned.TryGetValue(first, out var firstNeighbours))
                planned[first] = firstNeighbours = [];
            firstNeighbours.Add(second);
            if (!planned.TryGetValue(second, out var secondNeighbours))
                planned[second] = secondNeighbours = [];
            secondNeighbours.Add(first);
        }
    }

    async Task ValidateNamespaceSlotsAsync(string ownerId, IEnumerable<string> neighbours)
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var neighbourId in neighbours) {
            var name = neighbourId[(neighbourId.LastIndexOf('/') + 1)..];
            if (slots.TryGetValue(name, out var previous) && previous != neighbourId)
                throw NamespaceConflict(ownerId, name, previous, neighbourId);
            slots[name] = neighbourId;
        }

        var existing = await ReadBackingAsync(ownerId).ConfigureAwait(false);
        if (existing is null)
            return;
        await foreach (var neighbour in existing.Nodes.ConfigureAwait(false)) {
            var name = neighbour.LocalId.ToString();
            if (slots.TryGetValue(name, out var intended) && intended != neighbour.GlobalId.ToString())
                throw NamespaceConflict(ownerId, name, neighbour.GlobalId.ToString(), intended);
        }
    }

    static TypedGraphException NamespaceConflict(string owner, string name, string first, string second) =>
        Error(TypedGraphError.Unsupported, $"Carrier namespace '{owner}' cannot use local name '{name}' for both '{first}' and '{second}'.");

    bool IsInfrastructureType(InternalId id) => id == graph.GetRequiredRuntimeType<InstanceOfEdge>().GlobalId
        || id == graph.GetRequiredRuntimeType<RequiresEdge>().GlobalId;

    static TypedElement Snapshot(TypedEdgeInstance relation) => new(relation.Relation.GlobalId.ToString(), TypedElementKind.Relation,
        Array.AsReadOnly(new[] { relation.Type.GlobalId.ToString() }), CopyAttributes(relation.Relation.Attributes),
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(relation.Endpoints.ToDictionary(static endpoint => endpoint.Definition.Name,
            static endpoint => (IReadOnlyList<string>)Array.AsReadOnly(endpoint.Participants.Select(static participant => participant.GlobalId.ToString()).Order(StringComparer.Ordinal).ToArray()), StringComparer.Ordinal)));

    static IReadOnlyDictionary<string, string> CopyAttributes(IEnumerable<KeyValuePair<string, string>> attributes) =>
        new ReadOnlyDictionary<string, string>(attributes.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyMembers() =>
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    static NodePath ParseId(string id)
    {
        var segments = id.Split('/');
        if (segments.Any(static segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw Error(TypedGraphError.Invalid, $"'{id}' is not a canonical carrier identity.");
        return new NodePath(segments);
    }

    static NodeLocalId RequireLocalId(string id)
    {
        var parsed = ParseId(id);
        if (parsed.Count() != 1)
            throw Error(TypedGraphError.Unsupported, "Creation requires a single local name.");
        return new NodeLocalId(id);
    }

    static void EnsureCaseUnique(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (!seen.Add(name))
                throw Error(TypedGraphError.Unsupported, $"The carrier representation cannot distinguish case-variant name '{name}'.");
    }

    static Type ToClrType(ScalarKind kind) => kind switch {
        ScalarKind.String => typeof(string), ScalarKind.Boolean => typeof(bool), ScalarKind.Int32 => typeof(int),
        ScalarKind.Int64 => typeof(long), ScalarKind.Double => typeof(double), ScalarKind.Decimal => typeof(decimal),
        ScalarKind.Guid => typeof(Guid), ScalarKind.DateTime => typeof(DateTime),
        _ => throw Error(TypedGraphError.Unsupported, $"Unsupported scalar kind '{kind}'.")
    };

    static ScalarKind ToScalarKind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        foreach (var kind in Enum.GetValues<ScalarKind>())
            if (ToClrType(kind) == type)
                return kind;
        throw Error(TypedGraphError.Unsupported, $"Unsupported scalar CLR type '{type}'.");
    }

    static T Require<T>(ServiceResult<T> result, TypedGraphError invalidError = TypedGraphError.Invalid)
    {
        if (result.Status == ServiceResultStatus.Ok && result.Value is not null)
            return result.Value;
        throw ResultError(result.Status, result.Error, invalidError);
    }

    static void Require(ServiceResult result)
    {
        if (result.Status != ServiceResultStatus.Ok)
            throw ResultError(result.Status, result.Error, TypedGraphError.Invalid);
    }

    static TypedGraphException ResultError(ServiceResultStatus status, string? message, TypedGraphError invalidError) => Error(status switch {
        ServiceResultStatus.NotFound => TypedGraphError.NotFound,
        ServiceResultStatus.Conflict => TypedGraphError.Conflict,
        ServiceResultStatus.BadRequest => invalidError,
        _ => TypedGraphError.Corrupt
    }, message ?? $"Carrier operation failed: {status}.");

    static TypedGraphException Error(TypedGraphError kind, string message) => new(kind, message);
}
