using System.Text.Json;
using Abstractions;
using GraphData.Core.Services;
using GraphData.Mcp.Tools;
using GraphData.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class GraphDataMcpToolWrapperTests : StorageTests {
    [TestMethod]
    public async Task RawTools_CreateConnectReadAndDeleteUntypedNodes() {
        var rawTools = new GraphDataRawTools(await CreateTools());

        AssertSuccess(await rawTools.CreateNode("RawA"));
        AssertSuccess(await rawTools.CreateNode("RawB"));
        AssertSuccess(await rawTools.ConnectNodes(["RawA"], ["RawB"]));

        var subgraphJson = await rawTools.GetSubgraph([["RawA"]], maxDepth: 1);
        using (var subgraphDocument = JsonDocument.Parse(subgraphJson)) {
            var root = subgraphDocument.RootElement;
            Assert.IsTrue(root.GetProperty("success").GetBoolean(), subgraphJson);

            var nodeIds = root.GetProperty("nodes")
                .EnumerateArray()
                .Select(static node => node.GetProperty("internalId").GetString())
                .ToArray();
            Assert.IsTrue(nodeIds.Contains("RawA"), subgraphJson);
            Assert.IsTrue(nodeIds.Contains("RawB"), subgraphJson);
            Assert.AreEqual(1, root.GetProperty("edges").EnumerateArray().Count(), subgraphJson);
        }

        AssertSuccess(await rawTools.DeleteNode(["RawB"]));
        AssertNodeNotFound(await rawTools.GetNode(["RawB"]));
    }

    [TestMethod]
    public async Task SemanticTools_CreateTypedNodeWithValidatedFields() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        AssertSuccess(await semanticTools.CreateType("SemanticCountry"));
        var manufacturerFields = JsonSerializer.SerializeToElement(new {
            Country = new {
                valueKind = "Node",
                nodeType = "NodeTypes/SemanticCountry",
                cardinality = "required"
            },
            FoundedYear = new {
                valueKind = "Primitive",
                clrType = "int",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("SemanticManufacturer", fields: manufacturerFields));
        AssertSuccess(await semanticTools.CreateNode("SemanticUSSR", type: "NodeTypes/SemanticCountry"));

        var nodeFields = JsonSerializer.SerializeToElement(new {
            Country = "SemanticUSSR",
            FoundedYear = 1807
        });
        var createJson = await semanticTools.CreateNode(
            "SemanticKalashnikov",
            type: "NodeTypes/SemanticManufacturer",
            fields: nodeFields);

        AssertSuccess(createJson);
        using var getDocument = JsonDocument.Parse(await semanticTools.GetNode(["SemanticKalashnikov"]));
        Assert.IsTrue(getDocument.RootElement.GetProperty("found").GetBoolean());
    }

    [TestMethod]
    public async Task SemanticTools_RejectNodeFieldReferencesWithoutCreatingMissingNodes() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        AssertSuccess(await semanticTools.CreateType("NoChainCountry"));
        var manufacturerFields = JsonSerializer.SerializeToElement(new {
            Country = new {
                valueKind = "Node",
                nodeType = "NodeTypes/NoChainCountry",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("NoChainManufacturer", fields: manufacturerFields));

        var nodeFields = JsonSerializer.SerializeToElement(new {
            Country = "MissingCountry"
        });
        var createJson = await semanticTools.CreateNode(
            "ManufacturerWithMissingCountry",
            type: "NodeTypes/NoChainManufacturer",
            fields: nodeFields);

        AssertFailure(createJson, "NotFound");
        AssertNodeNotFound(await semanticTools.GetNode(["MissingCountry"]));
        AssertNodeNotFound(await semanticTools.GetNode(["ManufacturerWithMissingCountry"]));
    }

    [TestMethod]
    public async Task SemanticTools_RejectInvalidPrimitiveFieldWithoutCreatingNode() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        var fields = JsonSerializer.SerializeToElement(new {
            Year = new {
                valueKind = "Primitive",
                clrType = "int",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("PrimitiveWeapon", fields: fields));

        var nodeFields = JsonSerializer.SerializeToElement(new {
            Year = "nineteen forty seven"
        });
        var createJson = await semanticTools.CreateNode(
            "BadPrimitiveWeapon",
            type: "NodeTypes/PrimitiveWeapon",
            fields: nodeFields);

        AssertFailure(createJson, "BadRequest");
        AssertNodeNotFound(await semanticTools.GetNode(["BadPrimitiveWeapon"]));
    }

    [TestMethod]
    public async Task SemanticTools_RejectFieldsWithoutType() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());
        var fields = JsonSerializer.SerializeToElement(new {
            Country = "USSR"
        });

        var createJson = await semanticTools.CreateNode("FieldsWithoutType", fields: fields);

        AssertFailure(createJson, "BadRequest");
        AssertNodeNotFound(await semanticTools.GetNode(["FieldsWithoutType"]));
    }

    private async Task<GraphDataTools> CreateTools() {
        var graph = await global::Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        return new GraphDataTools(new GraphService(graph, new GraphSearchService(Storage)), graph);
    }

    private static void AssertSuccess(string json) {
        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean(), json);
    }

    private static void AssertFailure(string json, string status) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.IsFalse(root.GetProperty("success").GetBoolean(), json);
        Assert.AreEqual(status, root.GetProperty("status").GetString(), json);
    }

    private static void AssertNodeNotFound(string json) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.IsFalse(root.GetProperty("found").GetBoolean(), json);
    }
}
