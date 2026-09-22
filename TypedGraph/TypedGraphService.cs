using System.Collections.ObjectModel;
using System.Globalization;

namespace GraphData.Typed;

/// <summary>One semantic policy shared by both representations. No storage grammar lives here.</summary>
public sealed class TypedGraphService(ITypedGraphStore store) : ITypedGraph, IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);

    public Task<TypedType> GetTypeAsync(string id) => Locked(async () => {
        TypedGraphRules.ValidateId(id);
        return Copy(await RequireType(id));
    });

    public Task<IReadOnlyList<TypedType>> GetTypesAsync() => Locked<IReadOnlyList<TypedType>>(async () => {
        var types = await store.ReadTypesAsync();
        RequireUnique(types.Select(type => type.Id), "type IDs");
        foreach (var type in types)
            ValidateStoredType(type);
        return types.OrderBy(type => type.Id, StringComparer.Ordinal).Select(Copy).ToArray();
    });

    public Task<TypedElement> GetAsync(string id) => Locked(async () => {
        TypedGraphRules.ValidateId(id);
        return Copy(await RequireValidElement(id));
    });

    public Task<IReadOnlyList<TypedElement>> FindIncidentRelationsAsync(string participantId) => Locked<IReadOnlyList<TypedElement>>(async () => {
        TypedGraphRules.ValidateId(participantId);
        await RequireValidElement(participantId);
        var relations = await store.ReadIncidentRelationsAsync(participantId);
        RequireUnique(relations.Select(relation => relation.Id), "incident relation IDs");
        foreach (var relation in relations) {
            await ValidateElement(relation, persisted: true);
            if (relation.Kind != TypedElementKind.Relation || !relation.Members.Values.Any(ids => ids.Contains(participantId, StringComparer.Ordinal)))
                throw Error(TypedGraphError.Corrupt, $"Incident lookup returned unrelated element '{relation.Id}'.");
        }
        return relations.OrderBy(relation => relation.Id, StringComparer.Ordinal).Select(Copy).ToArray();
    });

    public Task<TypedType> CreateTypeAsync(TypedType definition) => Locked(async () => {
        var type = Copy(definition);
        ValidateTypeShape(type);
        var parts = type.Id.Split('/');
        if (parts.Length != 2 || parts[0] != "NodeTypes")
            throw Error(TypedGraphError.Unsupported, "New types must have an ID of the form NodeTypes/<name> in the shared catalog.");
        if (new[] { "Definition", "Fields", "Slots" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
            throw Error(TypedGraphError.Unsupported, $"Type name '{parts[1]}' is reserved by the carrier format.");
        if (await store.ReadTypeAsync(type.Id) is not null)
            throw Error(TypedGraphError.Conflict, $"Type '{type.Id}' already exists.");
        foreach (var requiredId in type.RequiredTypeIds) {
            var required = await RequireType(requiredId);
            if (required.Kind != type.Kind)
                throw Error(TypedGraphError.Invalid, "Instance and relation type hierarchies are separate.");
        }
        foreach (var member in type.Members)
            if (member.TypeId is { } memberTypeId)
                await RequireType(memberTypeId);
        await store.InsertTypeAsync(type);
        return Copy(await RequireType(type.Id));
    });

    public Task<TypedElement> CreateInstanceAsync(string id, IReadOnlyList<string> typeIds, IReadOnlyDictionary<string, string> attributes) => Locked(async () => {
        ValidateNewElementId(id);
        RequireUnique(typeIds, "assigned type IDs");
        if (typeIds.Count == 0)
            throw Error(TypedGraphError.Invalid, "An instance must have at least one type.");
        foreach (var typeId in typeIds) {
            var type = await RequireType(typeId);
            if (type.IsAbstract)
                throw Error(TypedGraphError.Invalid, $"Abstract type '{typeId}' cannot be assigned directly.");
        }
        var effective = await Closure(typeIds);
        var element = new TypedElement(id, TypedElementKind.Instance, effective.Select(type => type.Id).ToArray(),
            CopyAttributes(attributes), EmptyMembers());
        await ValidateElement(element, persisted: false);
        await store.InsertElementAsync(element);
        return Copy(await RequireValidElement(id));
    });

    public Task<TypedElement> CreateRelationAsync(string id, string typeId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, IReadOnlyDictionary<string, string> attributes) => Locked(async () => {
        ValidateNewElementId(id);
        var type = await RequireType(typeId);
        if (type.IsAbstract)
            throw Error(TypedGraphError.Invalid, $"Abstract relation type '{typeId}' cannot be instantiated.");
        var element = new TypedElement(id, TypedElementKind.Relation, [typeId], CopyAttributes(attributes), CopyMembers(members));
        await ValidateElement(element, persisted: false);
        await store.InsertElementAsync(element);
        return Copy(await RequireValidElement(id));
    });

    public Task<TypedElement> ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes) => Locked(async () => {
        TypedGraphRules.ValidateId(id);
        var existing = await RequireValidElement(id);
        var replacement = existing with { Attributes = CopyAttributes(attributes) };
        await ValidateElement(replacement, persisted: false);
        await store.ReplaceAttributesAsync(id, replacement.Attributes);
        return Copy(await RequireValidElement(id));
    });

    public async Task DeleteAsync(string id) => await Locked(async () => {
        TypedGraphRules.ValidateId(id);
        await RequireValidElement(id);
        var references = await store.ReadIncidentRelationsAsync(id);
        if (references.Count != 0)
            throw Error(TypedGraphError.Conflict, $"Element '{id}' is referenced by relations: {string.Join(", ", references.Select(relation => relation.Id).Order(StringComparer.Ordinal))}. Delete those relations explicitly first.");
        await store.DeleteElementAsync(id);
        return true;
    });

    async Task<TypedType> RequireType(string id) {
        TypedGraphRules.ValidateId(id);
        var type = await store.ReadTypeAsync(id)
            ?? throw Error(TypedGraphError.NotFound, $"Type '{id}' does not exist in this catalog.");
        if (type.Id != id)
            throw Error(TypedGraphError.Corrupt, $"Type lookup '{id}' returned '{type.Id}'.");
        ValidateStoredType(type);
        return type;
    }

    async Task<TypedElement> RequireValidElement(string id) {
        var element = await store.ReadElementAsync(id)
            ?? throw Error(TypedGraphError.NotFound, $"Element '{id}' does not exist.");
        if (element.Id != id)
            throw Error(TypedGraphError.Corrupt, $"Element lookup '{id}' returned '{element.Id}'.");
        await ValidateElement(element, persisted: true);
        return element;
    }

    async Task<IReadOnlyList<TypedType>> Closure(IEnumerable<string> ids) {
        var result = new Dictionary<string, TypedType>(StringComparer.Ordinal);
        var pending = new Queue<string>(ids);
        while (pending.TryDequeue(out var id)) {
            if (result.ContainsKey(id))
                continue;
            var type = await RequireType(id);
            result.Add(id, type);
            foreach (var required in type.RequiredTypeIds)
                pending.Enqueue(required);
        }
        return result.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();
    }

    async Task ValidateElement(TypedElement element, bool persisted) {
        try {
            await ValidateElementCore(element, persisted);
        } catch (TypedGraphException error) when (persisted && error.Error is TypedGraphError.Invalid or TypedGraphError.NotFound) {
            throw Error(TypedGraphError.Corrupt, $"Stored element '{element.Id}' is invalid: {error.Message}");
        }
    }

    async Task ValidateElementCore(TypedElement element, bool persisted) {
        TypedGraphRules.ValidateId(element.Id);
        if (!Enum.IsDefined(element.Kind))
            throw Error(TypedGraphError.Corrupt, $"Unknown kind for '{element.Id}'.");
        RequireUnique(element.TypeIds, "element type IDs");
        if (element.TypeIds.Count == 0)
            throw Error(persisted ? TypedGraphError.Corrupt : TypedGraphError.Invalid, $"Element '{element.Id}' has no types.");
        var types = await Closure(element.TypeIds);
        if (types.Any(type => type.Kind != element.Kind))
            throw Error(TypedGraphError.Invalid, $"Element '{element.Id}' mixes instance and relation types.");
        if (!types.Select(type => type.Id).ToHashSet(StringComparer.Ordinal).SetEquals(element.TypeIds))
            throw Error(TypedGraphError.Corrupt, $"Element '{element.Id}' is missing materialized required types; repair it explicitly.");
        ValidateAttributes(element.Attributes, types);
        if (element.Kind == TypedElementKind.Instance) {
            if (element.Members.Count != 0)
                throw Error(TypedGraphError.Invalid, "Instance members are not supported; model named links as relations.");
            return;
        }
        if (types.Count != 1)
            throw Error(TypedGraphError.Unsupported, "Combining multiple relation schemas is not defined in the shared contract.");
        var relationType = types[0];
        if (!relationType.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal).SetEquals(element.Members.Keys))
            throw Error(TypedGraphError.Invalid, $"Relation '{element.Id}' must supply exactly the declared member names.");
        foreach (var member in relationType.Members) {
            var participants = element.Members[member.Name];
            RequireUnique(participants, $"participants of '{member.Name}'");
            if (participants.Count < member.Min || member.Max is { } max && participants.Count > max)
                throw Error(TypedGraphError.Invalid, $"Member '{member.Name}' expects {member.Min}..{member.Max?.ToString() ?? "*"} participants, got {participants.Count}.");
            foreach (var participantId in participants) {
                TypedGraphRules.ValidateId(participantId);
                var participant = await store.ReadElementAsync(participantId)
                    ?? throw Error(TypedGraphError.Invalid, $"Member '{member.Name}' references missing participant '{participantId}'.");
                if (participant.Id != participantId || participant.TypeIds.Count == 0 || !Enum.IsDefined(participant.Kind))
                    throw Error(TypedGraphError.Corrupt, $"Invalid participant '{participantId}'.");
                var participantTypes = await Closure(participant.TypeIds);
                if (!participantTypes.Select(type => type.Id).ToHashSet(StringComparer.Ordinal).SetEquals(participant.TypeIds)
                    || participantTypes.Any(type => type.Kind != participant.Kind))
                    throw Error(TypedGraphError.Corrupt, $"Participant '{participantId}' has invalid materialized types.");
                try {
                    ValidateAttributes(participant.Attributes, participantTypes);
                    if (participant.Kind == TypedElementKind.Instance && participant.Members.Count != 0)
                        throw Error(TypedGraphError.Invalid, "Instance has unexpected relation members.");
                    if (participant.Kind == TypedElementKind.Relation) {
                        if (participantTypes.Count != 1)
                            throw Error(TypedGraphError.Unsupported, "Multiple relation schemas are not defined.");
                        var participantSchema = participantTypes[0];
                        if (!participantSchema.Members.Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(participant.Members.Keys))
                            throw Error(TypedGraphError.Invalid, "Relation has unexpected member names.");
                        foreach (var item in participantSchema.Members) {
                            var ids = participant.Members[item.Name];
                            RequireUnique(ids, $"participants of '{item.Name}'");
                            if (ids.Count < item.Min || item.Max is { } limit && ids.Count > limit)
                                throw Error(TypedGraphError.Invalid, "Relation member cardinality is invalid.");
                        }
                    }
                } catch (TypedGraphException error) when (error.Error == TypedGraphError.Invalid) {
                    throw Error(TypedGraphError.Corrupt, $"Participant '{participantId}' is invalid: {error.Message}");
                }
                if (member.TypeId is { } expected && !participant.TypeIds.Contains(expected, StringComparer.Ordinal))
                    throw Error(TypedGraphError.Invalid, $"Member '{member.Name}' requires type '{expected}', but participant '{participantId}' does not have it.");
            }
        }
    }

    static void ValidateStoredType(TypedType type) {
        try { ValidateTypeShape(type); }
        catch (TypedGraphException error) when (error.Error == TypedGraphError.Invalid) {
            throw Error(TypedGraphError.Corrupt, $"Stored type '{type.Id}' is invalid: {error.Message}");
        }
    }

    static void ValidateTypeShape(TypedType type) {
        TypedGraphRules.ValidateId(type.Id);
        if (!Enum.IsDefined(type.Kind))
            throw Error(TypedGraphError.Invalid, "Unknown type kind.");
        RequireUnique(type.RequiredTypeIds, "required type IDs");
        foreach (var required in type.RequiredTypeIds)
            TypedGraphRules.ValidateId(required);
        RequireUnique(type.Members.Select(member => member.Name), "member names");
        RequireUnique(type.Attributes.Select(attribute => attribute.Name), "attribute names");
        if (type.Kind == TypedElementKind.Instance && type.Members.Count != 0)
            throw Error(TypedGraphError.Unsupported, "Named node fields have no portable carrier encoding yet; use a relation type.");
        if (type.Kind == TypedElementKind.Relation) {
            if (type.RequiredTypeIds.Count != 0)
                throw Error(TypedGraphError.Unsupported, "Inheritance of relation member schemas is not defined in the shared contract.");
            if (type.Members.Count < 2)
                throw Error(TypedGraphError.Invalid, "A relation type must define at least two named members.");
        }
        foreach (var member in type.Members) {
            TypedGraphRules.ValidateName(member.Name);
            if (member.Min < 0 || member.Max is { } max && (max < member.Min || max < 0))
                throw Error(TypedGraphError.Invalid, $"Invalid cardinality for '{member.Name}'.");
            if (member.TypeId is { } typeId)
                TypedGraphRules.ValidateId(typeId);
        }
        foreach (var attribute in type.Attributes) {
            TypedGraphRules.ValidateName(attribute.Name);
            if (!Enum.IsDefined(attribute.Kind))
                throw Error(TypedGraphError.Unsupported, $"Unknown scalar kind for '{attribute.Name}'.");
        }
        if (type.Members.Select(member => member.Name).Intersect(type.Attributes.Select(attribute => attribute.Name), StringComparer.OrdinalIgnoreCase).Any())
            throw Error(TypedGraphError.Invalid, "Members and attributes must have distinct names.");
    }

    static void ValidateAttributes(IReadOnlyDictionary<string, string> attributes, IEnumerable<TypedType> types) {
        RequireUnique(attributes.Keys, "attribute names");
        foreach (var key in attributes.Keys)
            TypedGraphRules.ValidateName(key);
        var fields = types.SelectMany(type => type.Attributes).ToArray();
        foreach (var names in fields.Select(field => field.Name).GroupBy(name => name, StringComparer.OrdinalIgnoreCase))
            if (names.Distinct(StringComparer.Ordinal).Count() > 1)
                throw Error(TypedGraphError.Invalid, $"Type schemas disagree on the case of attribute '{names.Key}'.");
        foreach (var group in fields.GroupBy(field => field.Name, StringComparer.Ordinal)) {
            if (group.Select(field => field.Kind).Distinct().Count() != 1)
                throw Error(TypedGraphError.Invalid, $"Conflicting scalar definitions for '{group.Key}'.");
            var field = group.First();
            if (attributes.Keys.Any(name => string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase) && name != field.Name))
                throw Error(TypedGraphError.Invalid, $"Use the declared spelling '{field.Name}' for this attribute.");
            if (!attributes.TryGetValue(field.Name, out var value)) {
                if (group.Any(item => item.Required))
                    throw Error(TypedGraphError.Invalid, $"Required attribute '{field.Name}' is missing.");
                continue;
            }
            var valid = field.Kind switch {
                ScalarKind.String => true,
                ScalarKind.Boolean => bool.TryParse(value, out _),
                ScalarKind.Int32 => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                ScalarKind.Int64 => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                ScalarKind.Double => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
                ScalarKind.Decimal => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
                ScalarKind.Guid => Guid.TryParseExact(value, "D", out _),
                ScalarKind.DateTime => DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                _ => false
            };
            if (!valid)
                throw Error(TypedGraphError.Invalid, $"Attribute '{field.Name}' is not a valid {field.Kind} value.");
        }
    }

    static void ValidateNewElementId(string id) {
        TypedGraphRules.ValidateId(id);
        if (id.Contains('/') || string.Equals(id, "NodeTypes", StringComparison.OrdinalIgnoreCase))
            throw Error(TypedGraphError.Unsupported, "New shared-contract elements use a single local ID; namespace ownership is not portable yet.");
    }

    static void RequireUnique(IEnumerable<string> values, string description) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            if (!seen.Add(value))
                throw Error(TypedGraphError.Invalid, $"Duplicate or case-colliding {description}: '{value}'.");
    }

    static TypedGraphException Error(TypedGraphError error, string message) => new(error, message);
    static TypedType Copy(TypedType type) => type with { RequiredTypeIds = type.RequiredTypeIds.ToArray(), Members = type.Members.ToArray(), Attributes = type.Attributes.ToArray() };
    static TypedElement Copy(TypedElement element) => element with { TypeIds = element.TypeIds.ToArray(), Attributes = CopyAttributes(element.Attributes), Members = CopyMembers(element.Members) };
    static IReadOnlyDictionary<string, string> CopyAttributes(IReadOnlyDictionary<string, string> values) => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal));
    static IReadOnlyDictionary<string, IReadOnlyList<string>> CopyMembers(IReadOnlyDictionary<string, IReadOnlyList<string>> values) => new ReadOnlyDictionary<string, IReadOnlyList<string>>(values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal));
    static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyMembers() => new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    async Task<T> Locked<T>(Func<Task<T>> operation) {
        await gate.WaitAsync();
        try { return await operation(); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}

/// <summary>Canonical addresses shared with the carrier filesystem. Never normalize or guess an ID.</summary>
public static class TypedGraphRules
{
    public static void ValidateId(string id) {
        if (string.IsNullOrWhiteSpace(id))
            throw new TypedGraphException(TypedGraphError.Invalid, "An ID is required.");
        foreach (var segment in id.Split('/'))
            ValidateName(segment);
    }

    public static void ValidateName(string name) {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name is "." or ".."
            || name.Any(character => !char.IsLetterOrDigit(character) && character is not (' ' or '.' or '_' or '-')))
            throw new TypedGraphException(TypedGraphError.Invalid, $"Invalid graph name '{name}'.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new TypedGraphException(TypedGraphError.Invalid, $"Reserved graph name '{name}'.");
    }
}
