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
    public async Task SemanticTools_CreateTypedNodeWithValidatedPrimitiveFields() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());

        var manufacturerFields = JsonSerializer.SerializeToElement(new {
            FoundedYear = new {
                valueKind = "Primitive",
                clrType = "int",
                cardinality = "required"
            }
        });
        AssertSuccess(await semanticTools.CreateType("SemanticManufacturer", fields: manufacturerFields));

        var nodeFields = JsonSerializer.SerializeToElement(new {
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
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/SemanticManufacturer" },
            node.GetProperty("declaredTypeIds")
                .EnumerateArray()
                .Select(static type => type.GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/SemanticManufacturer" },
            node.GetProperty("effectiveTypeIds")
                .EnumerateArray()
                .Select(static type => type.GetString())
                .ToArray());
        Assert.IsFalse(node.TryGetProperty("types", out _));
    }

    [TestMethod]
    public async Task SemanticTools_CreateTypeWithRequires_SeparatesDeclaredAndEffectiveTypes() {
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
        var node = nodeDocument.RootElement.GetProperty("node");
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/RequiredDerived" },
            node.GetProperty("declaredTypeIds")
                .EnumerateArray()
                .Select(static type => type.GetString())
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "NodeTypes/RequiredBase", "NodeTypes/RequiredDerived" },
            node.GetProperty("effectiveTypeIds")
                .EnumerateArray()
                .Select(static type => type.GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task SemanticTools_CreateRelation_ReturnsCanonicalNaryMembers() {
        var graph = await global::Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        var service = new GraphService(graph, new GraphSearchService(Storage));
        var semanticTools = new GraphDataSemanticTools(new GraphDataTools(service));
        var relationTypeFields = JsonSerializer.SerializeToElement(new {
            Source = new {
                valueKind = "Node",
                cardinality = "required"
            },
            Cargo = new {
                valueKind = "Node",
                cardinality = "oneOrMore"
            }
        });
        var createTypeJson = await semanticTools.CreateType(
            "McpShipment",
            fields: relationTypeFields,
            elementKind: "Edge");
        AssertSuccess(createTypeJson);
        using (var typeDocument = JsonDocument.Parse(createTypeJson)) {
            var createdType = typeDocument.RootElement.GetProperty("type");
            Assert.AreEqual(
                "NodeTypes/McpShipment",
                createdType.GetProperty("internalId").GetString());
            Assert.AreEqual("Edge", createdType.GetProperty("elementKind").GetString());
        }
        await service.CreateNode("factory");
        await service.CreateNode("rifle-1");
        await service.CreateNode("rifle-2");
        var members = JsonSerializer.SerializeToElement(new {
            Source = "factory",
            Cargo = new[] { "rifle-1", "rifle-2" }
        });

        var json = await semanticTools.CreateRelation(
            "shipment-1",
            "NodeTypes/McpShipment",
            members);

        AssertSuccess(json);
        using var document = JsonDocument.Parse(json);
        var relation = document.RootElement.GetProperty("relation");
        Assert.AreEqual("shipment-1", relation.GetProperty("internalId").GetString());
        Assert.AreEqual("Relation", relation.GetProperty("semanticKind").GetString());
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/McpShipment" },
            relation.GetProperty("typeIds")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());

        var relationMembers = relation.GetProperty("relationMembers")
            .EnumerateArray()
            .ToDictionary(
                static member => member.GetProperty("name").GetString()!,
                static member => member,
                StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            new[] { "factory" },
            relationMembers["Source"].GetProperty("participantIds")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "rifle-1", "rifle-2" },
            relationMembers["Cargo"].GetProperty("participantIds")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.IsTrue(relationMembers.Values.All(static member =>
            !string.IsNullOrWhiteSpace(member.GetProperty("memberTypeId").GetString())));
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
            document.RootElement.GetProperty("node").GetProperty("effectiveTypeIds")
                .EnumerateArray()
                .Select(static type => type.GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task SemanticTools_RejectNodeValuedFieldsBeforeCreatingTheInstance() {
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
        AssertSuccess(await semanticTools.CreateNode("ExistingCountry", type: "NodeTypes/NoChainCountry"));

        var nodeFields = JsonSerializer.SerializeToElement(new {
            Country = "ExistingCountry"
        });
        var createJson = await semanticTools.CreateNode(
            "ManufacturerWithMissingCountry",
            type: "NodeTypes/NoChainManufacturer",
            fields: nodeFields);

        AssertFailure(createJson, "BadRequest");
        using (var document = JsonDocument.Parse(createJson)) {
            var error = document.RootElement.GetProperty("error").GetString();
            StringAssert.Contains(error, "named node fields");
            StringAssert.Contains(error, "CreateRelation");
        }
        AssertNodeNotFound(await semanticTools.GetNode(["ManufacturerWithMissingCountry"]));
    }

    [TestMethod]
    public async Task SemanticTools_AllowsOmittedOptionalNodeFieldAsEmpty() {
        var semanticTools = new GraphDataSemanticTools(await CreateTools());
        AssertSuccess(await semanticTools.CreateType("OptionalMcpTarget"));
        var fields = JsonSerializer.SerializeToElement(new {
            Target = new {
                valueKind = "Node",
                nodeType = "NodeTypes/OptionalMcpTarget",
                cardinality = "optional"
            }
        });
        AssertSuccess(await semanticTools.CreateType("OptionalMcpOwner", fields: fields));

        var createJson = await semanticTools.CreateNode(
            "OptionalMcpInstance",
            type: "NodeTypes/OptionalMcpOwner");

        AssertSuccess(createJson);
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
    public async Task SemanticTools_ReportRequiredNodeFieldsAsUnsupportedBeforeMutation() {
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
            StringAssert.Contains(error, "Type 'NodeTypes/ValidationManufacturer' requires named node fields");
            StringAssert.Contains(error, "Country");
            StringAssert.Contains(error, "CreateRelation");
        }
        AssertNodeNotFound(await semanticTools.GetNode(["MissingRequiredFields"]));
    }

    private async Task<GraphDataTools> CreateTools() {
        var graph = await global::Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        return new GraphDataTools(new GraphService(graph, new GraphSearchService(Storage)));
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
