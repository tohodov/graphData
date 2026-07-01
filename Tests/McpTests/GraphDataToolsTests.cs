using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Mcp.Tools;
using GraphData.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class GraphDataToolsTests : StorageTests
{
    [TestMethod]
    public async Task GetNode_ReturnsApiNodeShapeWithoutSerializingNodeCycles()
    {
        var first = await Storage.Create(new NodeLocalId("1"));
        var second = await Storage.Create(new NodeLocalId("2"));
        await Storage.Connect(first.GlobalId, second.GlobalId);

        var graph = await Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        var tools = new GraphDataTools(new GraphService(graph, new GraphSearchService(Storage)));
        var json = await tools.GetNode(["1"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.IsTrue(root.GetProperty("found").GetBoolean());

        var node = root.GetProperty("node");
        Assert.AreEqual("1", node.GetProperty("localId").GetString());
        Assert.AreEqual("1", node.GetProperty("internalId").GetString());
        Assert.IsTrue(node.TryGetProperty("attributes", out _));
        Assert.IsFalse(node.TryGetProperty("nodes", out _));

        var edges = node.GetProperty("edges").EnumerateArray().ToArray();
        Assert.AreEqual(1, edges.Length);
        Assert.AreEqual("1", edges[0].GetProperty("node1LocalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("node2LocalId").GetString());
        Assert.AreEqual("1", edges[0].GetProperty("node1InternalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("node2InternalId").GetString());
    }
}
