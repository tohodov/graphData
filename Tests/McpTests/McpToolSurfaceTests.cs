using System.Reflection;
using GraphData.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;

[RelevantTestClass]
public sealed class McpToolSurfaceTests {
    [TestMethod]
    public void SemanticTools_ExposeOnlySemanticSurface() {
        CollectionAssert.AreEqual(
            new[] {
                "CreateNode",
                "CreateType",
                "GetNode",
                "GetSubgraph",
                "GetTypeDefinitions",
                "SearchNodes"
            },
            GetToolMethodNames(typeof(GraphDataSemanticTools)));
    }

    [TestMethod]
    public void RawTools_ExposeOnlyRawSurface() {
        CollectionAssert.AreEqual(
            new[] {
                "ConnectNodes",
                "CreateNode",
                "DeleteNode",
                "GetNode",
                "GetSubgraph",
                "SearchNodes"
            },
            GetToolMethodNames(typeof(GraphDataRawTools)));
    }

    [TestMethod]
    public void AllTools_ExposeCompleteDevelopmentSurface() {
        CollectionAssert.AreEqual(
            new[] {
                "ConnectNodes",
                "CreateNode",
                "CreateType",
                "DeleteNode",
                "GetNode",
                "GetSubgraph",
                "GetTypeDefinitions",
                "SearchNodes"
            },
            GetToolMethodNames(typeof(GraphDataTools)));
    }

    [TestMethod]
    public void RawCreateNode_DoesNotExposeTypeOrFieldsParameters() {
        var createNode = typeof(GraphDataRawTools).GetMethod(nameof(GraphDataRawTools.CreateNode));
        Assert.IsNotNull(createNode);

        var parameterNames = createNode.GetParameters()
            .Select(static parameter => parameter.Name)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "localId", "path" }, parameterNames);
    }

    private static string[] GetToolMethodNames(Type toolType) {
        return toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(static method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
