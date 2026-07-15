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
                "CreateRelation",
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
                "CreateRelation",
                "CreateType",
                "DeleteNode",
                "DisconnectNodes",
                "GetNode",
                "GetSemanticNode",
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
    public void SemanticGetNode_UsesOnlyTheNodePath() {
        var getNode = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.GetNode));
        Assert.IsNotNull(getNode);

        var parameters = getNode.GetParameters();
        Assert.AreEqual(1, parameters.Length);
        Assert.AreEqual("path", parameters[0].Name);
        Assert.AreEqual(typeof(string[]), parameters[0].ParameterType);
    }

    [TestMethod]
    public void SemanticCreateRelation_ExposesCanonicalNamedMembers() {
        var createRelation = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.CreateRelation));
        Assert.IsNotNull(createRelation);

        var parameters = createRelation.GetParameters();
        CollectionAssert.AreEqual(
            new[] { "localId", "type", "members" },
            parameters.Select(static parameter => parameter.Name).ToArray());
        Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        Assert.AreEqual(typeof(System.Text.Json.JsonElement), parameters[2].ParameterType);
    }

    [TestMethod]
    public void SemanticCreateType_ExposesOptionalRequiresPaths() {
        var createType = typeof(GraphDataSemanticTools).GetMethod(nameof(GraphDataSemanticTools.CreateType));
        Assert.IsNotNull(createType);

        var requires = createType.GetParameters().Single(static parameter => parameter.Name == "requires");

        Assert.IsTrue(requires.HasDefaultValue);
        Assert.IsNull(requires.DefaultValue);
        Assert.AreEqual(typeof(string[]), requires.ParameterType);

        var elementKind = createType.GetParameters().Single(static parameter => parameter.Name == "elementKind");
        Assert.IsTrue(elementKind.HasDefaultValue);
        Assert.AreEqual("Node", elementKind.DefaultValue);
        Assert.AreEqual(typeof(string), elementKind.ParameterType);
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
