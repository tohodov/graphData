using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Mcp;

[TestClass]
public sealed class GraphDataToolsTests
{
    [TestMethod]
    public async Task GetNode_ReturnsApiNodeShapeWithoutSerializingNodeCycles()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new NodeLocalId("1"))).Value!;
        var second = (await scope.Storage.Create(new NodeLocalId("2"))).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var tools = new GraphDataTools(new GraphService(scope.Storage, new GraphSearchService(scope.Storage)));
        var json = await tools.GetNode(["1"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.IsTrue(root.GetProperty("found").GetBoolean());

        var node = root.GetProperty("node");
        Assert.AreEqual("1", node.GetProperty("localId").GetString());
        Assert.AreEqual("1", node.GetProperty("globalId").GetString());
        Assert.IsTrue(node.TryGetProperty("attributes", out _));
        Assert.IsFalse(node.TryGetProperty("nodes", out _));

        var edges = node.GetProperty("edges").EnumerateArray().ToArray();
        Assert.AreEqual(1, edges.Length);
        Assert.AreEqual("1", edges[0].GetProperty("sourceLocalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("targetLocalId").GetString());
        Assert.AreEqual("1", edges[0].GetProperty("sourceGlobalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("targetGlobalId").GetString());
    }
}
