using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class GraphControllerTests {
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
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["kind"] = "root" })).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["kind"] = "leaf" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetNodeAsync([first.LocalId]);

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
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var actions = (await scope.Storage.Create(new("actions"), root.GlobalId, new Dictionary<string, string> { ["kind"] = "child" })).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(root.GlobalId.ToString(), actions.LocalId.ToString());

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
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("Small_Arms_Web_KG"))).Value!;
        var actions = (await scope.Storage.Create(new("Actions"), root.GlobalId)).Value!;
        var gasOperated = (await scope.Storage.Create(new("Gas_Operated"), actions.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(
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
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(root.GlobalId.ToString(), "missing");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsConflictForAmbiguousLocalId() {
        var controller = CreateController(AmbiguousNeighborGraphStorage.Create());

        var result = await controller.GetNeighborNodeAsync("root", "same");

        var conflict = result.Result as ConflictObjectResult;
        Assert.IsNotNull(conflict);
        StringAssert.Contains(conflict.Value?.ToString(), "same");
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNotFoundForMissingNode() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync(["missing"]);

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsCreatedNodeWithLocation() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "new node",
            Attributes = new Dictionary<string, string> { ["kind"] = "demo" }
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        StringAssert.Contains(created.Location, "/api/graph/nodes?globalId=new%20node");

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("new node", response.LocalId);
        Assert.AreEqual("new node", response.InternalId);
        Assert.AreEqual("demo", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task CreateNodeAsync_CreatesChildWhenParentGlobalIdIsProvided() {
        await using var scope = TestGraphStorageScope.Create();
        var parent = (await scope.Storage.Create(new("parent"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "child",
            ParentPath = [parent.LocalId]
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("child", response.LocalId);
        Assert.AreEqual("parent/child", response.InternalId);

        var stored = await scope.Storage.Get(new NodePath("parent","child"));
        Assert.IsNotNull(stored);
    }

    [TestMethod]
    public async Task GetNodeAsync_ResolvesChildByGlobalIdSegments() {
        await using var scope = TestGraphStorageScope.Create();
        var parent = (await scope.Storage.Create(new("parent"))).Value!;
        var child = (await scope.Storage.Create(new("child"), parent.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync(["parent", "child"]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(child.LocalId.ToString(), response.LocalId);
        Assert.AreEqual(child.GlobalId.ToString(), response.InternalId);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForInvalidNodeName() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "KG Test: Ручное стрелковое оружие"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
        StringAssert.Contains(message, NodeNameValidator.AllowedSegmentCharactersDescription);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForReservedNodeNameSegment() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
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
        await using var scope = TestGraphStorageScope.Create();
        var source = (await scope.Storage.Create(new("source"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
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
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("small_arms_test_graph"))).Value!;
        await scope.Storage.Create(new("weapons"), root.GlobalId);
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = ["small_arms_test_graph"],
            Node2InternalId = ["small_arms_test_graph", "weapons"]
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        var rootPath = Path.Combine(scope.RootPath, "small_arms_test_graph");
        var childPath = Path.Combine(rootPath, "weapons");
        Assert.IsTrue(Directory.Exists(childPath));
        Assert.IsFalse(File.GetAttributes(childPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(rootPath)
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ConnectsNestedSiblingsWithSymLinkStorage() {
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("small_arms_test_graph"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var categories = (await scope.Storage.Create(new("categories"), root.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = ["small_arms_test_graph", "weapons"],
            Node2InternalId = ["small_arms_test_graph", "categories"]
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        weapons = (await scope.Storage.Get(new NodePath("small_arms_test_graph", "weapons"))).Value!;
        categories = (await scope.Storage.Get(new NodePath("small_arms_test_graph", "categories"))).Value!;

        var weaponLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons", "categories");
        var categoryLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "categories", "weapons");
        Assert.IsTrue(File.GetAttributes(weaponLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(categoryLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "categories"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));

        var weaponConnections = (await scope.Storage.GetConnectedNodesAsync(weapons)).Value!;
        var categoryConnections = (await scope.Storage.GetConnectedNodesAsync(categories)).Value!;
        Assert.IsTrue(weaponConnections.Any(node => node.LocalId == categories.LocalId));
        Assert.IsTrue(categoryConnections.Any(node => node.LocalId == weapons.LocalId));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsInternalErrorDetailsWhenConnectFails() {
        await using var scope = TestGraphStorageScope.Create();
        var source = (await scope.Storage.Create(new("source"))).Value!;
        var target = (await scope.Storage.Create(new("target"))).Value!;
        var controller = CreateController(new ConnectThrowingGraphStorage(scope.Storage));

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
        await using var scope = TestGraphStorageScope.Create();
        await CreatePathAsync(scope.Storage, GraphBaseTypeIds.NodeType);
        var weaponType = (await scope.Storage.Create(new("Weapon"), GraphSystemNodeIds.NodeTypeRoot)).Value!;
        await scope.Storage.Connect(weaponType.GlobalId, GraphBaseTypeIds.NodeType);
        var ak47 = (await scope.Storage.Create(new("ak-47"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.AssignNodeTypeAsync(new AssignNodeTypeRequest {
            InternalId = ak47.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = weaponType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        Assert.IsTrue(response.Nodes.Any(node => node.InternalId == ak47.GlobalId.ToString()));
        Assert.IsTrue(response.Nodes.Any(node => node.InternalId == weaponType.GlobalId.ToString()));

        var storedNode = (await scope.Storage.Get(ak47.GlobalId)).Value!;
        Assert.IsFalse(storedNode.Attributes.ContainsKey("graph.typeName"));

        var connected = (await scope.Storage.GetConnectedNodesAsync(storedNode)).Value!;
        Assert.IsTrue(connected.Any(node => node.GlobalId == weaponType.GlobalId));
    }

    [TestMethod]
    public async Task ChangeEdgeTypeAsync_CreatesTypedEdgeSubgraphForBasicEdgeAndReturnsIt() {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage).InitializeAsync();
        var newType = (await scope.Storage.Create(new("new-type"), GraphSystemNodeIds.NodeTypeRoot)).Value!;
        await scope.Storage.Connect(newType.GlobalId, GraphBaseTypeIds.NodeType);
        await scope.Storage.Connect(newType.GlobalId, GraphBaseTypeIds.Connection);
        var source = (await scope.Storage.Create(new("source"))).Value!;
        var target = (await scope.Storage.Create(new("target"))).Value!;
        await scope.Storage.Connect(source.GlobalId, target.GlobalId);
        var controller = CreateController(scope.Storage);

        var result = await controller.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            Node1InternalId = source.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Node2InternalId = target.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = newType.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypedEdgeLocalId = "typed-edge-1"
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
        Assert.IsTrue(endpointPorts.All(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, GraphBaseTypeIds.Port.ToString()))));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, source.GlobalId.ToString()))));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, target.GlobalId.ToString()))));

        var storedRelation = (await scope.Storage.Get(new NodePath("typed-edge-1"))).Value!;
        Assert.AreEqual(0, storedRelation.Attributes.Count);

        var replacementType = (await scope.Storage.Create(new("replacement-type"), GraphSystemNodeIds.NodeTypeRoot)).Value!;
        await scope.Storage.Connect(replacementType.GlobalId, GraphBaseTypeIds.NodeType);
        await scope.Storage.Connect(replacementType.GlobalId, GraphBaseTypeIds.Connection);
        await scope.Storage.Update(storedRelation.GlobalId, new Dictionary<string, string> { ["note"] = "user note" });
        var retyped = await controller.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            TypedEdgeGlobalId = storedRelation.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = replacementType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });
        Assert.IsInstanceOfType(retyped.Result, typeof(OkObjectResult));

        var sourceConnections = (await scope.Storage.GetConnectedNodesAsync(source)).Value!;
        Assert.IsFalse(sourceConnections.Any(node => node.GlobalId == target.GlobalId));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsNodeEdgesForLazyExpansionAndTopLevelEdgesForLoadedNodes() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"))).Value!;
        var second = (await scope.Storage.Create(new("2"))).Value!;
        var third = (await scope.Storage.Create(new("3"))).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);
        await scope.Storage.Connect(second.GlobalId, third.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
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
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var categories = (await scope.Storage.Create(new("categories"), root.GlobalId)).Value!;
        var ak47 = (await scope.Storage.Create(new("ak_47"), weapons.GlobalId)).Value!;
        var assaultRifle = (await scope.Storage.Create(new("assault_rifle"), categories.GlobalId)).Value!;
        await scope.Storage.Connect(ak47.GlobalId, assaultRifle.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
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
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var ak47 = (await scope.Storage.Create(new("ak_47"), weapons.GlobalId)).Value!;

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
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
        await using var scope = TestGraphStorageScope.Create();
        var firstRoot = (await scope.Storage.Create(new("first"))).Value!;
        var secondRoot = (await scope.Storage.Create(new("second"))).Value!;
        var child = (await scope.Storage.Create(new("child"), firstRoot.GlobalId)).Value!;

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [],
            MaxDepth = 0
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId.ToString(), secondRoot.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());
        Assert.AreEqual(0, response.Edges.Count);

        var firstNode = response.Nodes.Single(node => node.InternalId == firstRoot.GlobalId.ToString());
        var secondNode = response.Nodes.Single(node => node.InternalId == secondRoot.GlobalId.ToString());
        Assert.IsTrue(firstNode.Edges.Any(edge => HasEndpoints(edge, firstRoot.GlobalId.ToString(), child.GlobalId.ToString())));
        Assert.AreEqual(0, secondNode.Edges.Count);
    }

    [TestMethod]
    public async Task GetSubgraphAsync_EmptyGlobalIdSelectorReturnsTopLevelRoots() {
        await using var scope = TestGraphStorageScope.Create();
        var firstRoot = (await scope.Storage.Create(new("first"))).Value!;
        var secondRoot = (await scope.Storage.Create(new("second"))).Value!;
        await scope.Storage.Create(new("child"), firstRoot.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
            Paths = [[]],
            MaxDepth = 0
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId.ToString(), secondRoot.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsVariableBindings() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["id"] = "source" })).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var matches = await SearchNodesAsync(controller, new NodeSearchQueryRequest {
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
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"))).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var matches = await SearchNodesAsync(controller, new NodeSearchQueryRequest {
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

    private static GraphController CreateController(IGraphStorage storage) {
        var controller = new GraphController(new GraphService(storage, new GraphSearchService(storage), new CancellationTokensAccessorMock()));
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static async Task CreatePathAsync(IGraphStorage storage, InternalId id)
    {
        var segments = id.ToArray();
        NodePath? parent = null;
        for (var index = 0; index < segments.Length; index++) {
            var current = new InternalId(segments.Take(index + 1));
            var existing = await storage.Get(current);
            if (existing.Status == ServiceResultStatus.NotFound) {
                var create = await storage.Create(segments[index], parent);
                Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
            }

            parent = current;
        }
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatchResponse>> SearchNodesAsync(
        GraphController controller,
        NodeSearchQueryRequest query) {
        var result = await controller.SearchNodesAsync(query, CancellationToken.None);

        Assert.IsInstanceOfType(result.Result, typeof(EmptyResult));
        StringAssert.Contains(controller.Response.ContentType, "application/x-ndjson");

        controller.Response.Body.Position = 0;
        using var reader = new StreamReader(controller.Response.Body, Encoding.UTF8, leaveOpen: true);
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

    private sealed class SymLinkGraphStorageScope : IAsyncDisposable {
        private readonly string _rootPath;

        private SymLinkGraphStorageScope(string rootPath) {
            _rootPath = rootPath;
            Storage = new SymLinkGraphStorage(
                Options.Create(new NtfsGraphStorageOptions { RootPath = rootPath }),
                new CancellationTokensAccessorMock(),
                NullLogger<SymLinkGraphStorage>.Instance);
        }

        public IGraphStorage Storage { get; }

        public string RootPath => _rootPath;

        public static SymLinkGraphStorageScope Create() {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "GraphDataTests",
                "SymLinkApi",
                Guid.NewGuid().ToString("N"));
            return new SymLinkGraphStorageScope(rootPath);
        }

        public ValueTask DisposeAsync() {
            DeleteDirectoryWithoutFollowingLinks(new DirectoryInfo(_rootPath));
            return ValueTask.CompletedTask;
        }

        private static void DeleteDirectoryWithoutFollowingLinks(DirectoryInfo directory) {
            if (!directory.Exists) {
                return;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos()) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    entry.Delete();
                    continue;
                }

                if (entry is DirectoryInfo childDirectory) {
                    DeleteDirectoryWithoutFollowingLinks(childDirectory);
                } else {
                    entry.Delete();
                }
            }

            directory.Delete();
        }
    }

    private sealed class ConnectThrowingGraphStorage(IGraphStorage inner) : IGraphStorage {
        public Task<ServiceResult<NodeState>> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null) =>
            inner.Create(name, parent, attributes);

        public Task<ServiceResult<NodeState>> Get(NodeRef query) => inner.Get(query);

        public Task<ServiceResult> Delete(NodeRef query) => inner.Delete(query);

        public Task<ServiceResult> Connect(NodeRef sourcePath, NodeRef targetPath) =>
            Task.FromResult(ServiceResult.InternalServerError(new InvalidOperationException("diagnostic connect failure").ToString()));

        public Task<ServiceResult> Disconnect(NodeRef sourcePath, NodeRef targetPath) =>
            Task.FromResult(ServiceResult.InternalServerError(new InvalidOperationException("diagnostic connect failure").ToString()));

        public Task<ServiceResult<IReadOnlyCollection<NodeState>>> GetConnectedNodesAsync(NodeState node) =>
            inner.GetConnectedNodesAsync(node);
    }

    private sealed class AmbiguousNeighborGraphStorage : IGraphStorage {
        private readonly StaticNode _root;

        private AmbiguousNeighborGraphStorage(StaticNode root) {
            _root = root;
        }

        public static IGraphStorage Create() {
            var root = new StaticNode(new("root"), new("root"));
            var first = new StaticNode(new("same"), new("left", "same"));
            var second = new StaticNode(new("same"), new("right", "same"));
            root.EdgeSnapshot = [
                new EdgeState(root, first),
                new EdgeState(root, second)
            ];

            return new AmbiguousNeighborGraphStorage(root);
        }

        public Task<ServiceResult<NodeState>> Get(NodeRef path) =>
            Task.FromResult(path.Equals(_root.GlobalId)
                ? ServiceResult<NodeState>.Ok(_root)
                : ServiceResult<NodeState>.NotFound());

        public Task<ServiceResult<NodeState>> Create(NodeLocalId name, NodeRef? NodePath = null, IDictionary<string, string>? attributes = null) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Delete(NodeRef path) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Connect(NodeRef sourcePath, NodeRef targetPath) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Disconnect(NodeRef sourcePath, NodeRef targetPath) =>
            throw new NotSupportedException();

        public Task<ServiceResult<IReadOnlyCollection<NodeState>>> GetConnectedNodesAsync(NodeState node) =>
            throw new NotSupportedException();
    }

    private sealed class StaticNode(NodeLocalId localId, InternalId globalId) : NodeState {
        public override NodeLocalId LocalId { get; } = localId;

        public override InternalId GlobalId { get; } = globalId;

        public ICollection<EdgeState> EdgeSnapshot { get; set; } = Array.Empty<EdgeState>();

        public override ICollection<EdgeState> Edges => EdgeSnapshot;

        public override ILazyCollection<NodeState> Nodes => new LazyList<NodeState>(Edges
            .SelectMany(static edge => new[] { edge.Node1, edge.Node2 })
            .Where(node => node.GlobalId != GlobalId)
            .ToArray());

        public override IDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
