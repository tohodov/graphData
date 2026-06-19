using System.Reflection;
using Abstractions;

namespace GraphData.Core.Models;

internal static class NodeTypeFieldDiscovery
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static void AddDiscoveredFields(
        Type nodeType,
        NodeTypeBuilder builder,
        Func<Type, NodeGlobalId> resolveTypeId)
    {
        foreach (var member in GetDslMembers(nodeType)) {
            var memberType = GetMemberType(member);
            var valueType = TryGetCollectionElementType(memberType, out var elementType)
                ? elementType
                : memberType;
            var cardinality = CreateCardinality(member, memberType, valueType);

            if (typeof(NodeType).IsAssignableFrom(valueType)) {
                builder.Field(new NodeFieldDefinition(
                    member.Name,
                    NodeFieldValueKind.Node,
                    valueType,
                    cardinality,
                    IsCollectionType(memberType),
                    resolveTypeId(valueType)));
                continue;
            }

            if (typeof(Node).IsAssignableFrom(valueType)) {
                builder.Field(new NodeFieldDefinition(
                    member.Name,
                    NodeFieldValueKind.Node,
                    valueType,
                    cardinality,
                    IsCollectionType(memberType)));
                continue;
            }

            if (IsPrimitiveValue(valueType)) {
                builder.Field(new NodeFieldDefinition(
                    member.Name,
                    NodeFieldValueKind.Primitive,
                    valueType,
                    cardinality,
                    IsCollectionType(memberType)));
            }
        }
    }

    private static IEnumerable<MemberInfo> GetDslMembers(Type nodeType)
    {
        var stack = new Stack<Type>();
        for (var current = nodeType; current is not null && current != typeof(NodeType) && current != typeof(Node); current = current.BaseType)
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

    private static bool IsPrimitiveValue(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan);
    }
}
