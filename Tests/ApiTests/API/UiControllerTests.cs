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
        await new GraphStorageInitializer(CreateService(schemaRegistry), schemaRegistry).InitializeAsync();
        await new GraphStorageInitializer(CreateService(schemaRegistry), schemaRegistry).InitializeAsync();

        var requestService = CreateService(schemaRegistry);
        var controller = new UiController(requestService);

        var result = controller.GetSettings();

        var settings = result.Value;
        Assert.IsNotNull(settings);
        Assert.AreEqual("graphdata", settings.SystemNodeIds.GraphDataRoot);
        Assert.AreEqual("graphdata/types", settings.SystemNodeIds.TypeRoot);
        Assert.AreEqual("graphdata/types/nodes", settings.SystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("graphdata/storage", settings.SystemNodeIds.StorageRoot);
        Assert.AreEqual("graphdata/storage/initializers", settings.SystemNodeIds.InitializerRoot);
        Assert.AreEqual("graphdata/storage/initializers/runtime-types", settings.SystemNodeIds.RuntimeTypesInitializer);
        Assert.AreEqual("graphdata/types/nodes", settings.Basis.NodeTypeRoot);
        Assert.IsNotNull(await Storage.Get(new NodePath("graphdata")));
        Assert.AreEqual("Storage.NodeFileSystem", requestService.GraphDataRoot.Backing.GetType().FullName);
        Assert.AreEqual("Storage.NodeFileSystem", requestService.RuntimeTypesInitializer.Backing.GetType().FullName);
    }

    private GraphData.Core.Services.GraphService CreateService(GraphSchemaRegistry schemaRegistry) =>
        new(
            Storage,
            new GraphSearchService(Storage),
            new CancellationTokensAccessorMock(),
            schemaRegistry);
}
