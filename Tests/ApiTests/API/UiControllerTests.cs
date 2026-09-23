
using GraphData.Api.Controllers;
using GraphData.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class UiControllerTests : StorageTests {
    [TestMethod]
    public async Task GetSettings_ReturnsMaterializedSystemNodeIdsForRequestScopedGraph() {
        var schemaRegistry = GraphSchemaRegistry.Create();

        var controller = new UiController(await CreateGraph(schemaRegistry));

        var result = controller.GetSettings();

        var settings = result.Value;
        Assert.IsNotNull(settings);
        Assert.AreEqual("raw", settings.GraphApi);
        Assert.AreEqual("NodeTypes", settings.SystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("", settings.SystemNodeIds.StorageRoot);
        Assert.AreEqual("NodeTypes", settings.Basis.NodeTypeRoot);
        Assert.IsNotNull(await Storage.Get(new NodePath("NodeTypes")));
    }

    [TestMethod]
    public void GetSettings_WithoutRawGraph_SelectsTypedApiAndNoSystemNodes() {
        using var services = new ServiceCollection().BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<UiController>(services);

        var settings = controller.GetSettings().Value;

        Assert.IsNotNull(settings);
        Assert.AreEqual("typed", settings.GraphApi);
        Assert.AreEqual("", settings.SystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("", settings.SystemNodeIds.StorageRoot);
        Assert.AreEqual("", settings.Basis.NodeTypeRoot);
    }

    Task<Graph> CreateGraph(GraphSchemaRegistry schemaRegistry) =>
        Graph.OpenAsync(Storage, schemaRegistry);
}
