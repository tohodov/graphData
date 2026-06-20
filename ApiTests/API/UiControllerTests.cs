using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class UiControllerTests
{
    [TestMethod]
    public void GetSettings_ReturnsBackendSystemNodeIdsForUi()
    {
        var controller = new UiController();

        var result = controller.GetSettings();

        var settings = result.Value as UiSettingsResponse;
        Assert.IsNotNull(settings);
        Assert.AreEqual(GraphSystemNodeIds.NodeTypeRoot.ToString(), settings.Basis.NodeTypeRoot);
        Assert.AreEqual(GraphSystemNodeIds.RuntimeTypesInitializer.ToString(), settings.SystemNodeIds.RuntimeTypesInitializer);
        Assert.AreEqual(GraphBaseTypeIds.NodeInstance.ToString(), settings.BaseTypeIds.NodeInstance);
        Assert.AreEqual(GraphBaseTypeIds.Relation.ToString(), settings.BaseTypeIds.EdgeType);
        Assert.AreEqual(GraphBaseTypeIds.Endpoint.ToString(), settings.BaseTypeIds.Endpoint);
        Assert.AreEqual(GraphBaseTypeIds.Port.ToString(), settings.BaseTypeIds.Port);
    }
}
