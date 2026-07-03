using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GraphData.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class ApiActionContractTests
{
    [TestMethod]
    public void PublicApiActions_DoNotReturnUntypedActionResults()
    {
        var violations = GetPublicApiActions()
            .Where(method =>
            {
                var returnType = UnwrapTask(method.ReturnType);
                return typeof(IActionResult).IsAssignableFrom(returnType);
            })
            .Select(static method => $"{method.DeclaringType?.FullName}.{method.Name}: {method.ReturnType}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            violations.Length,
            "Public API actions must return DTO-typed values, for example ActionResult<TDto>." +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void PublicApiContract_DoesNotExposeCoreTypes()
    {
        var actionReferences = GetPublicApiActions()
            .SelectMany(static method =>
            {
                var actionName = $"{method.DeclaringType?.FullName}.{method.Name}";
                var returnTypes = WalkType(method.ReturnType, $"{actionName} return", []);
                var parameterTypes = method.GetParameters()
                    .SelectMany(parameter => WalkType(parameter.ParameterType, $"{actionName} parameter '{parameter.Name}'", []));
                return returnTypes.Concat(parameterTypes);
            });

        var modelReferences = GetPublicApiModelTypes()
            .SelectMany(static type => WalkType(type, type.FullName ?? type.Name, []));

        var violations = actionReferences
            .Concat(modelReferences)
            .Where(static reference => IsCoreType(reference.Type))
            .Select(static reference => $"{reference.Path}: {FormatType(reference.Type)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            violations.Length,
            "Public API contract must not expose GraphData.Core types." +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<MethodInfo> GetPublicApiActions()
    {
        return typeof(RawGraphController).Assembly
            .GetTypes()
            .Where(static type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(static method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());
    }

    private static IEnumerable<Type> GetPublicApiModelTypes()
    {
        return typeof(RawGraphController).Assembly
            .GetExportedTypes()
            .Where(static type => string.Equals(type.Namespace, "GraphData.Api.Models", StringComparison.Ordinal));
    }

    private static IEnumerable<ContractTypeReference> WalkType(Type type, string path, HashSet<Type> visited)
    {
        if (type.IsByRef || type.IsPointer)
        {
            type = type.GetElementType()!;
        }

        if (type.IsArray)
        {
            foreach (var reference in WalkType(type.GetElementType()!, $"{path}[]", visited))
            {
                yield return reference;
            }

            yield break;
        }

        yield return new ContractTypeReference(path, type);

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var reference in WalkType(argument, $"{path}<{FormatType(argument)}>", visited))
                {
                    yield return reference;
                }
            }
        }

        if (!IsApiModelType(type) || !visited.Add(type))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is null)
            {
                continue;
            }

            foreach (var reference in WalkType(property.PropertyType, $"{path}.{property.Name}", visited))
            {
                yield return reference;
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            foreach (var reference in WalkType(field.FieldType, $"{path}.{field.Name}", visited))
            {
                yield return reference;
            }
        }
    }

    private static bool IsApiModelType(Type type)
    {
        return string.Equals(type.Namespace, "GraphData.Api.Models", StringComparison.Ordinal);
    }

    private static bool IsCoreType(Type type)
    {
        return type.Namespace?.StartsWith("GraphData.Core", StringComparison.Ordinal) == true;
    }

    private static string FormatType(Type type)
    {
        return type.FullName ?? type.Name;
    }

    private static Type UnwrapTask(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0]
            : type;
    }

    private sealed record ContractTypeReference(string Path, Type Type);
}
