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

[TestClass]
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

    private static IEnumerable<MethodInfo> GetPublicApiActions()
    {
        return typeof(GraphController).Assembly
            .GetTypes()
            .Where(static type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(static method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());
    }

    private static Type UnwrapTask(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0]
            : type;
    }
}
