using Domain.Services;
using GraphData.Api.Controllers;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class UiControllerTests : StorageTests {
    [TestMethod]
    public async Task GetSettings_ReturnsMaterializedSystemNodeIdsForRequestScopedGraphService() {
        var schemaRegistry = GraphSchemaRegistry.Create();
        await new GraphStorageInitializer(CreateService(schemaRegistry)).InitializeAsync();
        await new GraphStorageInitializer(CreateService(schemaRegistry)).InitializeAsync();

        var requestService = CreateService(schemaRegistry);
        var controller = new UiController(requestService);

        var result = controller.GetSettings();

        var settings = result.Value;
        Assert.IsNotNull(settings);
        Assert.AreEqual("NodeTypes", settings.SystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("", settings.SystemNodeIds.StorageRoot);
        Assert.AreEqual("NodeTypes", settings.Basis.NodeTypeRoot);
        Assert.IsNotNull(await Storage.Get(new NodePath("NodeTypes")));
    }

    private GraphData.Core.Services.GraphService CreateService(GraphSchemaRegistry schemaRegistry) =>
        new(
            Storage,
            new GraphSearchService(Storage),
            schemaRegistry);
}
