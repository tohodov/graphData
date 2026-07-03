using System.Text;
using System.Text.Json;
using Abstractions;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class GraphControllerTests : ControllerTests {
    [TestMethod]
    public void ConnectNodesRequest_DeserializesJsonGlobalIdArrays() {
        var request = JsonSerializer.Deserialize<ConnectNodesRequest>(
            """
            {
              "node1InternalId": [ "Small_Arms_Web_KG", "Weapons", "AK_47" ],
              "node2InternalId": [ "Small_Arms_Web_KG", "Categories", "Assault_Rifle" ]
            }
            """,
            GraphJsonSerializerOptions.Create());

        Assert.IsNotNull(request);
        Assert.AreEqual("Small_Arms_Web_KG/Weapons/AK_47", string.Join("/", request.Node1InternalId));
        Assert.AreEqual("Small_Arms_Web_KG/Categories/Assault_Rifle", string.Join("/", request.Node2InternalId));
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNodeWithNeighborEdges() {
        var first = await Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["kind"] = "root" });
        var second = await Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["kind"] = "leaf" });
        await Storage.Connect(first.GlobalId, second.GlobalId);

        var result = await Controller.GetNodeAsync([first.LocalId]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(first.LocalId.ToString(), response.LocalId);
        Assert.AreEqual(first.GlobalId.ToString(), response.InternalId);
        Assert.AreEqual("root", response.Attributes["kind"]);

        var edge = response.Edges.Single();
        Assert.AreEqual(second.LocalId.ToString(), edge.NeighborLocalId);
        Assert.AreEqual(first.LocalId.ToString(), edge.Node1LocalId);
        Assert.AreEqual(second.LocalId.ToString(), edge.Node2LocalId);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsNeighborByLocalId() {
        var root = await Storage.Create(new("root"));
        var actions = await Storage.Create(new("actions"), root.GlobalId, new Dictionary<string, string> { ["kind"] = "child" });

        var result = await Controller.GetNeighborNodeAsync(root.GlobalId.ToString(), actions.LocalId.ToString());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(actions.GlobalId.ToString(), response.InternalId);
        Assert.AreEqual(actions.LocalId.ToString(), response.LocalId);
        Assert.AreEqual("child", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_DecodesNestedGlobalIdRouteSegment() {
        var root = await Storage.Create(new("Small_Arms_Web_KG"));
        var actions = await Storage.Create(new("Actions"), root.GlobalId);
        var gasOperated = await Storage.Create(new("Gas_Operated"), actions.GlobalId);

        var result = await Controller.GetNeighborNodeAsync(
            Uri.EscapeDataString(actions.GlobalId.ToString()),
            gasOperated.LocalId.ToString());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(gasOperated.GlobalId.ToString(), response.InternalId);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsNotFoundForMissingNeighbor() {
        var root = await Storage.Create(new("root"));

        var result = await Controller.GetNeighborNodeAsync(root.GlobalId.ToString(), "missing");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNotFoundForMissingNode() {
        var result = await Controller.GetNodeAsync(["missing"]);

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsCreatedNodeWithLocation() {
        var result = await Controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "new node",
            Attributes = new Dictionary<string, string> { ["kind"] = "demo" }
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        StringAssert.Contains(created.Location, "/api/graph/raw/nodes?globalId=new%20node");

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("new node", response.LocalId);
        Assert.AreEqual("new node", response.InternalId);
        Assert.AreEqual("demo", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task CreateNodeAsync_CreatesChildWhenParentGlobalIdIsProvided() {
        var parent = await Storage.Create(new("parent"));

        var result = await Controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "child",
            ParentPath = [parent.LocalId]
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("child", response.LocalId);
        Assert.AreEqual("parent/child", response.InternalId);

        var stored = await Storage.Get(new NodePath("parent", "child"));
        Assert.IsNotNull(stored);
    }

    [TestMethod]
    public async Task GetNodeAsync_ResolvesChildByGlobalIdSegments() {
        var parent = await Storage.Create(new("parent"));
        var child = await Storage.Create(new("child"), parent.GlobalId);

        var result = await Controller.GetNodeAsync(["parent", "child"]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(child.LocalId.ToString(), response.LocalId);
        Assert.AreEqual(child.GlobalId.ToString(), response.InternalId);
    }

    [TestMethod]
    public async Task UpdateNodeAsync_AcceptsGlobalIdQuery() {
        var node = await Storage.Create(new("node"), attributes: new Dictionary<string, string> {
            ["kind"] = "original"
        });

        var result = await Controller.UpdateNodeAsync([node.LocalId.ToString()], [], new UpdateNodeRequest {
            Attributes = new Dictionary<string, string> {
                ["kind"] = "updated"
            }
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));
        var stored = await Storage.Get(node.GlobalId);
        Assert.IsNotNull(stored);
        Assert.AreEqual("updated", stored.Attributes["kind"]);
    }

    [TestMethod]
    public async Task DeleteNodeAsync_AcceptsGlobalIdQuery() {
        var node = await Storage.Create(new("node"));

        var result = await Controller.DeleteNodeAsync([node.LocalId.ToString()], []);

        Assert.IsInstanceOfType(result.Result, typeof(OkResult));
        Assert.IsNull(await Storage.Get(node.GlobalId));
        Assert.IsTrue(Directory.Exists(StorageOptions.RootPath));
    }

    [TestMethod]
    public async Task DeleteNodeAsync_RejectsMissingNodeIdentifierWithoutDeletingStorageRoot() {
        var node = await Storage.Create(new("node"));

        var result = await Controller.DeleteNodeAsync([], []);

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Node globalId is required.", badRequest.Value);
        Assert.IsTrue(Directory.Exists(StorageOptions.RootPath));
        Assert.IsNotNull(await Storage.Get(node.GlobalId));
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForInvalidNodeName() {
        var result = await Controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "KG Test: Ручное стрелковое оружие"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
        StringAssert.Contains(message, global::Storage.NodeNameValidator.AllowedSegmentCharactersDescription);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForReservedNodeNameSegment() {
        var result = await Controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "CON"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "segment 'CON' is reserved");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsBadRequestForInvalidTargetName() {
        var source = await Storage.Create(new("source"));

        var result = await Controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = [source.LocalId.ToString()],
            Node2InternalId = ["target:name"]
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "Target node path segment contains invalid character ':'");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsNoContentForExistingHierarchyConnectionWithSymLinkStorage() {
        var root = await Storage.Create(new("small_arms_test_graph"));
        await Storage.Create(new("weapons"), root.GlobalId);

        var result = await Controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = ["small_arms_test_graph"],
            Node2InternalId = ["small_arms_test_graph", "weapons"]
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        var rootPath = Path.Combine(StorageOptions.RootPath, "small_arms_test_graph");
        var childPath = Path.Combine(rootPath, "weapons");
        Assert.IsTrue(Directory.Exists(childPath));
        Assert.IsFalse(File.GetAttributes(childPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(rootPath)
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ConnectsNestedSiblingsWithSymLinkStorage() {
        var root = await Storage.Create(new("small_arms_test_graph"));
        var weapons = await Storage.Create(new("weapons"), root.GlobalId);
        var categories = await Storage.Create(new("categories"), root.GlobalId);

        var result = await Controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = ["small_arms_test_graph", "weapons"],
            Node2InternalId = ["small_arms_test_graph", "categories"]
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        weapons = await Storage.Get(new NodePath("small_arms_test_graph", "weapons"));
        Assert.IsNotNull(weapons);
        categories = await Storage.Get(new NodePath("small_arms_test_graph", "categories"));
        Assert.IsNotNull(categories);

        var weaponLinkPath = Path.Combine(StorageOptions.RootPath, "small_arms_test_graph", "weapons", "categories");
        var categoryLinkPath = Path.Combine(StorageOptions.RootPath, "small_arms_test_graph", "categories", "weapons");
        Assert.IsTrue(File.GetAttributes(weaponLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(categoryLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(StorageOptions.RootPath, "small_arms_test_graph", "weapons"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(StorageOptions.RootPath, "small_arms_test_graph", "categories"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));

        Assert.IsTrue(await weapons.Nodes.AnyAsync(node => node.LocalId == categories.LocalId));
        Assert.IsTrue(await categories.Nodes.AnyAsync(node => node.LocalId == weapons.LocalId));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsInternalErrorDetailsWhenConnectFails() {
        var source = await Storage.Create(new("source"));
        var target = await Storage.Create(new("target"));
        var controller = await CreateController(new ConnectThrowingGraphStorage(Storage));

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = [source.LocalId.ToString()],
            Node2InternalId = [target.LocalId.ToString()]
        });

        var objectResult = result.Result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var message = objectResult.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "InvalidOperationException");
        StringAssert.Contains(message, "diagnostic connect failure");
    }

    [TestMethod]
    public async Task AssignNodeTypeAsync_ConnectsNodeToTypeThroughDslValidator() {
        var typeRoot = await GetTypesRoot();
        var weaponType = await Storage.Create(new("Weapon"), typeRoot.GlobalId);
        var ak47 = await Storage.Create(new("ak-47"));

        var result = await SemanticController.AssignNodeTypeAsync(new AssignNodeTypeRequest {
            InternalId = ak47.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = weaponType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        Assert.IsTrue(response.Nodes.Any(node => node.InternalId == ak47.GlobalId.ToString()));
        Assert.IsTrue(response.Nodes.Any(node => node.InternalId == weaponType.GlobalId.ToString()));

        var storedNode = await Storage.Get(ak47.GlobalId);
        Assert.IsNotNull(storedNode);
        Assert.IsFalse(storedNode.Attributes.ContainsKey("graph.typeName"));
        Assert.IsTrue(await storedNode.Nodes.AnyAsync(node => node.GlobalId == weaponType.GlobalId));
    }

    [TestMethod]
    public async Task ChangeEdgeTypeAsync_CreatesTypedEdgeSubgraphForBasicEdgeAndReturnsIt() {
        var typeRoot = await GetTypesRoot();
        var newType = await Storage.Create(new("new-type"), typeRoot.GlobalId);
        var source = await Storage.Create(new("source"));
        var target = await Storage.Create(new("target"));
        await Storage.Connect(source.GlobalId, target.GlobalId);

        var result = await SemanticController.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            Node1InternalId = source.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Node2InternalId = target.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = newType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);

        var relation = response.Nodes.Single(node => node.InternalId == "typed-edge-1");
        Assert.AreEqual("typed-edge-1", relation.InternalId);
        Assert.AreEqual(0, relation.Attributes.Count);

        var returnedIds = response.Nodes.Select(static node => node.InternalId).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(returnedIds.Contains(source.GlobalId.ToString()));
        Assert.IsTrue(returnedIds.Contains(target.GlobalId.ToString()));
        Assert.IsTrue(returnedIds.Contains(newType.GlobalId.ToString()));

        var endpointPorts = response.Nodes
            .Where(node => GraphIdIsChildOf(node.InternalId, relation.InternalId) && node.InternalId != relation.InternalId)
            .ToArray();
        Assert.AreEqual(2, endpointPorts.Length);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, relation.InternalId, newType.GlobalId.ToString())));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, source.GlobalId.ToString()))));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, target.GlobalId.ToString()))));

        var storedRelation = await Storage.Get(new NodePath("typed-edge-1"));
        Assert.IsNotNull(storedRelation);
        Assert.AreEqual(0, storedRelation.Attributes.Count);

        var replacementType = await Storage.Create(new("replacement-type"), typeRoot.GlobalId);
        await Storage.Update(storedRelation.GlobalId, new Dictionary<string, string> { ["note"] = "user note" });
        var retyped = await SemanticController.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            Node1InternalId = source.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Node2InternalId = target.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = replacementType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });
        Assert.IsInstanceOfType(retyped.Result, typeof(OkObjectResult));
        Assert.IsFalse(await source.Nodes.AnyAsync(node => node.GlobalId == target.GlobalId));
    }

    [TestMethod]
    public async Task GetTypesAsync_ReturnsTypeClosureWithoutInstances() {
        var country = (await Service.CreateNodeType(new("CatalogCountry"))).Value!;
        var manufacturer = (await Service.CreateNodeType(
            new("CatalogManufacturer"),
            fields: [
                new NodeFieldDefinition(
                    "Country",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false,
                    country.Type)
            ])).Value!;
        var instance = await Storage.Create(new("USSR"));
        await Service.AssignNodeTypeAsync(instance.GlobalId, country.Type.GlobalId);

        var result = await SemanticController.GetTypesAsync(new TypeCatalogRequest {
            Roots = [Graph.NodeTypes.GlobalId.Select(static segment => segment.ToString()).ToArray()]
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as TypeCatalogResponse;
        Assert.IsNotNull(response);

        var typeIds = response.Types.Select(static type => type.InternalId).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(typeIds.Contains(country.Type.GlobalId.ToString()));
        Assert.IsTrue(typeIds.Contains(manufacturer.Type.GlobalId.ToString()));
        Assert.IsFalse(typeIds.Contains(instance.GlobalId.ToString()));

        Assert.IsTrue(response.References.Any(reference =>
            reference.Kind == "schemaReference"
            && reference.SourceTypeInternalId == manufacturer.Type.GlobalId.ToString()
            && reference.MemberKind == "field"
            && reference.MemberName == "Country"
            && reference.TargetTypeInternalId == country.Type.GlobalId.ToString()));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsNodeEdgesForLazyExpansionAndTopLevelEdgesForLoadedNodes() {
        var first = await Storage.Create(new("1"));
        var second = await Storage.Create(new("2"));
        var third = await Storage.Create(new("3"));
        await Storage.Connect(first.GlobalId, second.GlobalId);
        await Storage.Connect(second.GlobalId, third.GlobalId);

        var result = await Controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [[first.LocalId]],
            MaxDepth = 1
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(new[] { "1", "2" }, response.Nodes.Select(static node => node.LocalId).ToArray());

        var edge = response.Edges.Single();
        Assert.AreEqual(first.LocalId.ToString(), edge.Node1LocalId);
        Assert.AreEqual(second.LocalId.ToString(), edge.Node2LocalId);
        Assert.IsFalse(response.Edges.Any(edge => HasEndpoints(edge, second.GlobalId.ToString(), third.GlobalId.ToString())));

        var firstNode = response.Nodes.Single(static node => node.LocalId == "1");
        var secondNode = response.Nodes.Single(static node => node.LocalId == "2");
        Assert.IsTrue(firstNode.Edges.Any(edge => HasEndpoints(edge, first.GlobalId.ToString(), second.GlobalId.ToString())));
        Assert.IsTrue(secondNode.Edges.Any(edge => HasEndpoints(edge, first.GlobalId.ToString(), second.GlobalId.ToString())));
        Assert.IsTrue(secondNode.Edges.Any(edge => HasEndpoints(edge, second.GlobalId.ToString(), third.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsNestedNeighborEdgesWithGlobalIds() {
        var root = await Storage.Create(new("root"));
        var weapons = await Storage.Create(new("weapons"), root.GlobalId);
        var categories = await Storage.Create(new("categories"), root.GlobalId);
        var ak47 = await Storage.Create(new("ak_47"), weapons.GlobalId);
        var assaultRifle = await Storage.Create(new("assault_rifle"), categories.GlobalId);
        await Storage.Connect(ak47.GlobalId, assaultRifle.GlobalId);

        var result = await Controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [["root", "weapons", "ak_47"]],
            MaxDepth = 1
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString(), weapons.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());

        Assert.AreEqual(2, response.Edges.Count);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, weapons.GlobalId.ToString(), ak47.GlobalId.ToString())));

        var ak47Node = response.Nodes.Single(node => node.InternalId == ak47.GlobalId.ToString());
        var assaultRifleNode = response.Nodes.Single(node => node.InternalId == assaultRifle.GlobalId.ToString());
        Assert.IsTrue(ak47Node.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
        Assert.IsTrue(ak47Node.Edges.Any(edge => HasEndpoints(edge, weapons.GlobalId.ToString(), ak47.GlobalId.ToString())));
        Assert.IsTrue(assaultRifleNode.Edges.Any(edge => HasEndpoints(edge, categories.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_TraverseHierarchyEdges() {
        var root = await Storage.Create(new("root"));
        var weapons = await Storage.Create(new("weapons"), root.GlobalId);
        var ak47 = await Storage.Create(new("ak_47"), weapons.GlobalId);

        var result = await Controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [["root"]],
            MaxDepth = 2
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { root.GlobalId.ToString(), weapons.GlobalId.ToString(), ak47.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());
        Assert.AreEqual(2, response.Edges.Count);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, root.GlobalId.ToString(), weapons.GlobalId.ToString())));
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, weapons.GlobalId.ToString(), ak47.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_EmptyGlobalIdsReturnTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        var child = await Storage.Create(new("child"), firstRoot.GlobalId);

        var result = await Controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [],
            MaxDepth = 0
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new string[] { firstRoot.GlobalId, secondRoot.GlobalId, Graph.NodeTypes.GlobalId },
            response.Nodes.Select(static node => node.InternalId).ToArray());
        Assert.AreEqual(0, response.Edges.Count);

        var firstNode = response.Nodes.Single(node => node.InternalId == firstRoot.GlobalId.ToString());
        var secondNode = response.Nodes.Single(node => node.InternalId == secondRoot.GlobalId.ToString());
        Assert.IsTrue(firstNode.Edges.Any(edge => HasEndpoints(edge, firstRoot.GlobalId.ToString(), child.GlobalId.ToString())));
        Assert.AreEqual(0, secondNode.Edges.Count);
    }

    [TestMethod]
    public async Task GetSubgraphAsync_EmptyGlobalIdSelectorReturnsTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        await Storage.Create(new("child"), firstRoot.GlobalId);

        var result = await Controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [[]],
            MaxDepth = 0
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new string[] { firstRoot.GlobalId, secondRoot.GlobalId, Graph.NodeTypes.GlobalId },
            response.Nodes.Select(static node => node.InternalId).ToArray());
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsVariableBindings() {
        var first = await Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["id"] = "source" });
        var second = await Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" });
        await Storage.Connect(first.GlobalId, second.GlobalId);

        var matches = await SearchNodesAsync(Controller, new NodeSearchQueryRequest {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpressionRequest {
                Expressions = [
                    new NodeConnectedSearchExpressionRequest {
                        Left = new NodeVariableSearchSelectorRequest { Name = "n" },
                        Right = new NodeVariableSearchSelectorRequest { Name = "x" }
                    },
                    new NodeAttributeSearchExpressionRequest {
                        Node = new NodeVariableSearchSelectorRequest { Name = "x" },
                        Key = "id",
                        Value = "Y"
                    }
                ]
            }
        });

        var match = matches.Single();
        Assert.AreEqual(first.LocalId.ToString(), match.Bindings["n"].LocalId);
        Assert.AreEqual(second.LocalId.ToString(), match.Bindings["x"].LocalId);
        Assert.IsTrue(match.Bindings["n"].Edges.Any(edge => HasEndpoints(edge, first.GlobalId.ToString(), second.GlobalId.ToString())));
        Assert.IsTrue(match.Bindings["x"].Edges.Any(edge => HasEndpoints(edge, first.GlobalId.ToString(), second.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task SearchNodesAsync_WritesNdjsonMatches() {
        var first = await Storage.Create(new("1"));
        var second = await Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" });
        await Storage.Connect(first.GlobalId, second.GlobalId);

        var matches = await SearchNodesAsync(Controller, new NodeSearchQueryRequest {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpressionRequest {
                Expressions = [
                    new NodeConnectedSearchExpressionRequest {
                        Left = new NodeVariableSearchSelectorRequest { Name = "n" },
                        Right = new NodeVariableSearchSelectorRequest { Name = "x" }
                    },
                    new NodeAttributeSearchExpressionRequest {
                        Node = new NodeVariableSearchSelectorRequest { Name = "x" },
                        Key = "id",
                        Value = "Y"
                    }
                ]
            }
        });

        var streamed = matches.Single();
        Assert.AreEqual(first.LocalId.ToString(), streamed.Bindings["n"].LocalId);
        Assert.AreEqual(second.LocalId.ToString(), streamed.Bindings["x"].LocalId);
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatchResponse>> SearchNodesAsync(
        RawGraphController Controller,
        NodeSearchQueryRequest query) {
        var result = await Controller.SearchNodesAsync(query, CancellationToken.None);

        Assert.IsInstanceOfType(result.Result, typeof(EmptyResult));
        StringAssert.Contains(Controller.Response.ContentType, "application/x-ndjson");

        Controller.Response.Body.Position = 0;
        using var reader = new StreamReader(Controller.Response.Body, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync();

        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<NodeSearchMatchResponse>(
                line,
                GraphJsonSerializerOptions.Create()))
            .Cast<NodeSearchMatchResponse>()
            .ToArray();
    }

    private static bool HasEndpoints(EdgeResponse edge, string left, string right) =>
        (edge.Node1InternalId == left && edge.Node2InternalId == right) ||
        (edge.Node1InternalId == right && edge.Node2InternalId == left);

    private static bool GraphIdIsChildOf(string globalId, string parentGlobalId) =>
        globalId.StartsWith(parentGlobalId + "/", StringComparison.Ordinal);

    private sealed class ConnectThrowingGraphStorage(IGraphStorage inner) : IGraphStorage {
        public NodeBacking Root => inner.Root;
        public Task<NodeBacking> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null) => inner.Create(name, parent, attributes);
        public Task<NodeBacking?> Get(NodeRef query) => inner.Get(query);
        public Task Delete(NodeRef query) => inner.Delete(query);
        public Task Connect(NodeRef sourcePath, NodeRef targetPath) => throw new InvalidOperationException("diagnostic connect failure");
        public Task Disconnect(NodeRef sourcePath, NodeRef targetPath) => throw new InvalidOperationException("diagnostic connect failure");
        public IAsyncEnumerable<NodeBacking> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] others) => inner.GetCommonIntersection(first, second, others);
        public Task Delete(NodeBacking node) => inner.Delete(node);
        public IAsyncEnumerable<NodeBacking> EnumerateNodesAsync(CancellationToken cancellationToken = default) => inner.EnumerateNodesAsync(cancellationToken);
    }
}
