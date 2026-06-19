using System.Reflection;
using Abstractions;

namespace GraphData.Core.Models;

internal static class EdgeTypeEndpointDiscovery
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static void AddDiscoveredEndpoints(
        Type edgeType,
        EdgeTypeBuilder builder,
        Func<Type, NodeGlobalId> resolveNodeTypeId)
    {
        foreach (var member in GetDslMembers(edgeType)) {
            var memberType = GetMemberType(member);
            var valueType = TryGetCollectionElementType(memberType, out var elementType)
                ? elementType
                : memberType;
            if (!typeof(Node).IsAssignableFrom(valueType))
                continue;

            builder.Endpoint(new EdgeEndpointDefinition(
                member.Name,
                valueType,
                CreateCardinality(member, memberType, valueType),
                IsCollectionType(memberType),
                typeof(NodeType).IsAssignableFrom(valueType) ? resolveNodeTypeId(valueType) : null));
        }
    }

    private static IEnumerable<MemberInfo> GetDslMembers(Type edgeType)
    {
        var stack = new Stack<Type>();
        for (var current = edgeType; current is not null && current != typeof(EdgeType) && current != typeof(Edge); current = current.BaseType)
            stack.Push(current);

        while (stack.Count > 0) {
            var current = stack.Pop();
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                if (!field.IsStatic)
                    yield return field;
            }

            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
                    yield return property;
            }
        }
    }

    private static Type GetMemberType(MemberInfo member) =>
        member switch {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unsupported DSL member.")
        };

    private static NodeSlotCardinality CreateCardinality(MemberInfo member, Type memberType, Type valueType)
    {
        if (IsCollectionType(memberType))
            return NodeSlotCardinality.Many();

        if (IsNullable(member, memberType, valueType))
            return NodeSlotCardinality.Optional();

        return NodeSlotCardinality.Required();
    }

    private static bool IsNullable(MemberInfo member, Type memberType, Type valueType)
    {
        if (Nullable.GetUnderlyingType(memberType) is not null)
            return true;

        if (valueType.IsValueType)
            return false;

        var nullability = member switch {
            FieldInfo field => Nullability.Create(field),
            PropertyInfo property => Nullability.Create(property),
            _ => null
        };

        return nullability?.ReadState == NullabilityState.Nullable;
    }

    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type == typeof(string)) {
            elementType = type;
            return false;
        }

        if (type.IsArray) {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerableType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces()
                .FirstOrDefault(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableType is not null) {
            elementType = enumerableType.GetGenericArguments()[0];
            return true;
        }

        elementType = type;
        return false;
    }

    private static bool IsCollectionType(Type type) =>
        TryGetCollectionElementType(type, out _);
}
