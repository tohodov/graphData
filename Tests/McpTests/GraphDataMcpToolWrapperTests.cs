using System.Text.Json;
using Abstractions;
using GraphData.Core.Services;
using GraphData.Mcp.Tools;
using GraphData.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class GraphDataMcpToolWrapperTests : StorageTests {
    [TestMethod]
    public async Task RawTools_CreateConnectDisconnectReadAndDeleteUntypedNodes() {
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

        AssertSuccess(await rawTools.DisconnectNodes(["RawA"], ["RawB"]));
        var disconnectedSubgraphJson = await rawTools.GetSubgraph([["RawA"]], maxDepth: 1);
        using (var disconnectedDocument = JsonDocument.Parse(disconnectedSubgraphJson)) {
            var root = disconnectedDocument.RootElement;
            Assert.IsTrue(root.GetProperty("success").GetBoolean(), disconnectedSubgraphJson);
            Assert.AreEqual(0, root.GetProperty("edges").EnumerateArray().Count(), disconnectedSubgraphJson);
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
        var root = getDocument.RootElement;
        Assert.IsTrue(root.GetProperty("found").GetBoolean());
        var node = root.GetProperty("node");
        Assert.AreEqual("SemanticKalashnikov", node.GetProperty("localId").GetString());
        Assert.AreEqual("SemanticKalashnikov", node.GetProperty("internalId").GetString());
        var types = node.GetProperty("types").EnumerateArray().ToArray();
        Assert.AreEqual(1, types.Length);
        Assert.AreEqual("NodeTypes/SemanticManufacturer", types[0].GetProperty("typeInternalId").GetString());
        Assert.IsTrue(types[0].GetProperty("isMaterialized").GetBoolean());
        Assert.IsFalse(string.IsNullOrWhiteSpace(types[0].GetProperty("witnessInternalId").GetString()));
    }

    [TestMethod]
    public async Task SemanticTools_CreateTypeWithRequires_ProjectsRequiredAndMaterializedTypes() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        AssertSuccess(await semanticTools.CreateType("RequiredBase"));
        AssertSuccess(await semanticTools.CreateType(
            "RequiredDerived",
            requires: ["NodeTypes/RequiredBase"]));

        using (var definitionsDocument = JsonDocument.Parse(await semanticTools.GetTypeDefinitions())) {
            var derived = definitionsDocument.RootElement
                .GetProperty("types")
                .EnumerateArray()
                .Single(type => type.GetProperty("internalId").GetString() == "NodeTypes/RequiredDerived");
            var requiredTypeIds = derived
                .GetProperty("requiredTypeInternalIds")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "NodeTypes/RequiredBase" },
                requiredTypeIds);
        }

        AssertSuccess(await semanticTools.CreateNode(
            "RequiredInstance",
            type: "NodeTypes/RequiredDerived"));

        using var nodeDocument = JsonDocument.Parse(await semanticTools.GetNode(["RequiredInstance"]));
        var nodeTypes = nodeDocument.RootElement
            .GetProperty("node")
            .GetProperty("types")
            .EnumerateArray()
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "NodeTypes/RequiredBase", "NodeTypes/RequiredDerived" },
            nodeTypes.Select(static type => type.GetProperty("typeInternalId").GetString()).ToArray());
        Assert.IsTrue(nodeTypes.All(static type => type.GetProperty("isMaterialized").GetBoolean()));
        Assert.IsTrue(nodeTypes.All(static type =>
            !string.IsNullOrWhiteSpace(type.GetProperty("witnessInternalId").GetString())));
    }

    [TestMethod]
    public async Task SemanticTools_CreateNode_ValidatesFieldsFromRequiredTypes() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());
        var baseFields = JsonSerializer.SerializeToElement(new {
            BaseCode = new {
                valueKind = "Primitive",
                clrType = "string",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("FieldBase", fields: baseFields));
        AssertSuccess(await semanticTools.CreateType(
            "FieldDerived",
            requires: ["NodeTypes/FieldBase"]));

        var invalid = await semanticTools.CreateNode(
            "MissingBaseField",
            type: "NodeTypes/FieldDerived");
        AssertFailure(invalid, nameof(ServiceResultStatus.BadRequest));
        AssertNodeNotFound(await semanticTools.GetNode(["MissingBaseField"]));

        var fields = JsonSerializer.SerializeToElement(new { BaseCode = "base-value" });
        var valid = await semanticTools.CreateNode(
            "WithBaseField",
            type: "NodeTypes/FieldDerived",
            fields: fields);
        AssertSuccess(valid);
        using var document = JsonDocument.Parse(await semanticTools.GetNode(["WithBaseField"]));
        CollectionAssert.AreEquivalent(
            new[] { "NodeTypes/FieldBase", "NodeTypes/FieldDerived" },
            document.RootElement.GetProperty("node").GetProperty("types")
                .EnumerateArray()
                .Select(static type => type.GetProperty("typeInternalId").GetString())
                .ToArray());
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

        var createJson = await semanticTools.CreateNode("FieldsWithoutType", type: "", fields: fields);

        AssertFailure(createJson, "BadRequest");
        AssertNodeNotFound(await semanticTools.GetNode(["FieldsWithoutType"]));
    }

    [TestMethod]
    public async Task SemanticTools_ReportAllMissingRequiredFieldsWithExpectedTypes() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        AssertSuccess(await semanticTools.CreateType("ValidationCountry"));
        var fields = JsonSerializer.SerializeToElement(new {
            Country = new {
                valueKind = "Node",
                nodeType = "NodeTypes/ValidationCountry",
                cardinality = "required"
            },
            FoundedYear = new {
                valueKind = "Primitive",
                clrType = "int",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("ValidationManufacturer", fields: fields));

        var createJson = await semanticTools.CreateNode(
            "MissingRequiredFields",
            type: "NodeTypes/ValidationManufacturer");

        AssertFailure(createJson, "BadRequest");
        using (var document = JsonDocument.Parse(createJson)) {
            var error = document.RootElement.GetProperty("error").GetString();
            StringAssert.Contains(error, "Missing required fields for type 'NodeTypes/ValidationManufacturer'");
            StringAssert.Contains(error, "Country (Node: NodeTypes/ValidationCountry)");
            StringAssert.Contains(error, "FoundedYear (Primitive: Int32)");
        }
        AssertNodeNotFound(await semanticTools.GetNode(["MissingRequiredFields"]));
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
