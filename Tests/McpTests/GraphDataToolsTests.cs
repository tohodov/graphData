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
        var tools = new GraphDataTools(new GraphService(graph, new GraphSearchService(Storage)), graph);
        var json = await tools.GetNode(["1"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.IsTrue(root.GetProperty("found").GetBoolean());

        var node = root.GetProperty("node");
        Assert.AreEqual("1", node.GetProperty("localId").GetString());
        Assert.AreEqual("1", node.GetProperty("internalId").GetString());
        Assert.IsFalse(node.TryGetProperty("attributes", out _));
        Assert.IsFalse(node.TryGetProperty("nodes", out _));

        var edges = node.GetProperty("edges").EnumerateArray().ToArray();
        Assert.AreEqual(1, edges.Length);
        Assert.AreEqual("1", edges[0].GetProperty("node1LocalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("node2LocalId").GetString());
        Assert.AreEqual("1", edges[0].GetProperty("node1InternalId").GetString());
        Assert.AreEqual("2", edges[0].GetProperty("node2InternalId").GetString());
    }

    [TestMethod]
    public async Task CreateType_CreatesDynamicDefinitionVisibleToGetTypeDefinitions()
    {
        var graph = await Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        var tools = new GraphDataTools(new GraphService(graph, new GraphSearchService(Storage)), graph);

        var countryJson = await tools.CreateType("McpCountry");
        using var countryDocument = JsonDocument.Parse(countryJson);
        Assert.IsTrue(countryDocument.RootElement.GetProperty("success").GetBoolean(), countryJson);

        var manufacturerFields = JsonSerializer.SerializeToElement(new {
            Country = new {
                valueKind = "Node",
                nodeType = "NodeTypes/McpCountry",
                cardinality = "required"
            },
            FoundedYear = new {
                valueKind = "Primitive",
                clrType = "int",
                cardinality = "required"
            }
        });
        var manufacturerJson = await tools.CreateType("McpManufacturer", fields: manufacturerFields);
        using var manufacturerDocument = JsonDocument.Parse(manufacturerJson);
        Assert.IsTrue(manufacturerDocument.RootElement.GetProperty("success").GetBoolean(), manufacturerJson);

        var definitionsJson = await tools.GetTypeDefinitions();
        using var definitionsDocument = JsonDocument.Parse(definitionsJson);
        var manufacturer = definitionsDocument.RootElement
            .GetProperty("types")
            .EnumerateArray()
            .Single(type => type.GetProperty("internalId").GetString() == "NodeTypes/McpManufacturer");

        var fields = manufacturer.GetProperty("fields").EnumerateArray().ToArray();
        Assert.AreEqual(2, fields.Length);
        Assert.IsTrue(fields.Any(field =>
            field.GetProperty("name").GetString() == "Country"
            && field.GetProperty("nodeTypeInternalId").GetString() == "NodeTypes/McpCountry"));
        Assert.IsTrue(fields.Any(field =>
            field.GetProperty("name").GetString() == "FoundedYear"
            && field.GetProperty("valueKind").GetString() == "Primitive"));

        var countryNodeJson = await tools.CreateNode("USSR", type: "NodeTypes/McpCountry");
        using var countryNodeDocument = JsonDocument.Parse(countryNodeJson);
        Assert.IsTrue(countryNodeDocument.RootElement.GetProperty("success").GetBoolean(), countryNodeJson);

        var manufacturerNodeFields = JsonSerializer.SerializeToElement(new {
            Country = "USSR",
            FoundedYear = 1807
        });
        var manufacturerNodeJson = await tools.CreateNode(
            "Kalashnikov",
            type: "NodeTypes/McpManufacturer",
            fields: manufacturerNodeFields);
        using var manufacturerNodeDocument = JsonDocument.Parse(manufacturerNodeJson);
        Assert.IsTrue(manufacturerNodeDocument.RootElement.GetProperty("success").GetBoolean(), manufacturerNodeJson);
    }
}
