
using GraphData.Api.Controllers;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class UiControllerTests : StorageTests {
    [TestMethod]
    public async Task GetSettings_ReturnsMaterializedSystemNodeIdsForRequestScopedGraph() {
        var schemaRegistry = GraphSchemaRegistry.Create();

        var controller = new UiController(CreateGraphProvider(schemaRegistry));

        var result = await controller.GetSettings();

        var settings = result.Value;
        Assert.IsNotNull(settings);
        Assert.AreEqual("NodeTypes", settings.SystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("", settings.SystemNodeIds.StorageRoot);
        Assert.AreEqual("NodeTypes", settings.Basis.NodeTypeRoot);
        Assert.IsNotNull(await Storage.Get(new NodePath("NodeTypes")));
    }

    private GraphProvider CreateGraphProvider(GraphSchemaRegistry schemaRegistry) =>
        new(
            Storage,
            schemaRegistry);
}
