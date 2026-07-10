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
                "GetTypeDefinitions"
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
                "DisconnectNodes",
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
                "DisconnectNodes",
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

    [TestMethod]
    public void SemanticCreateNode_RequiresTypeParameter() {
        var createNode = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.CreateNode));
        Assert.IsNotNull(createNode);

        var type = createNode.GetParameters().Single(static parameter => parameter.Name == "type");

        Assert.IsFalse(type.HasDefaultValue);
        Assert.AreEqual(typeof(string), type.ParameterType);
    }

    [TestMethod]
    public void SemanticGetNode_ExposesOptionalBasisPaths() {
        var getNode = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.GetNode));
        Assert.IsNotNull(getNode);

        var basisTypes = getNode.GetParameters().Single(static parameter => parameter.Name == "basisTypes");

        Assert.IsTrue(basisTypes.HasDefaultValue);
        Assert.IsNull(basisTypes.DefaultValue);
        Assert.AreEqual(typeof(string[]), basisTypes.ParameterType);
    }

    [TestMethod]
    public void SemanticCreateType_ExposesOptionalRequiresPaths() {
        var createType = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.CreateType));
        Assert.IsNotNull(createType);

        var requires = createType.GetParameters().Single(static parameter => parameter.Name == "requires");

        Assert.IsTrue(requires.HasDefaultValue);
        Assert.IsNull(requires.DefaultValue);
        Assert.AreEqual(typeof(string[]), requires.ParameterType);
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
