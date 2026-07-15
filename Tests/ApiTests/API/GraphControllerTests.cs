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
        Assert.AreEqual("Instance", response.SemanticKind);
        Assert.AreEqual(0, response.TypeIds.Count);
        Assert.AreEqual(0, response.RelationMembers.Count);

        var edge = response.Edges.Single();
        Assert.AreEqual(second.LocalId.ToString(), edge.NeighborLocalId);
        Assert.AreEqual(first.LocalId.ToString(), edge.Node1LocalId);
        Assert.AreEqual(second.LocalId.ToString(), edge.Node2LocalId);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_DoesNotTreatAddressChildAsGraphNeighbor() {
        var root = await Storage.Create(new("root"));
        var actions = await Storage.Create(new("actions"), root.GlobalId, new Dictionary<string, string> { ["kind"] = "child" });

        var result = await Controller.GetNeighborNodeAsync(root.GlobalId.ToString(), actions.LocalId.ToString());

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_DecodesNestedGlobalIdRouteSegment() {
        var root = await Storage.Create(new("Small_Arms_Web_KG"));
        var actions = await Storage.Create(new("Actions"), root.GlobalId);
        var gasOperated = await Storage.Create(new("Gas_Operated"), root.GlobalId);
        await Storage.Connect(actions.GlobalId, gasOperated.GlobalId);

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
    public async Task GetNeighborNodeAsync_ResolvesSemanticRelationNeighborByLocalId() {
        var first = await Storage.Create(new("first"));
        var second = await Storage.Create(new("second"));
        await Storage.Connect(first.GlobalId, second.GlobalId);

        var result = await Controller.GetNeighborNodeAsync(
            first.GlobalId.ToString(),
            second.LocalId.ToString());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(second.GlobalId.ToString(), response.InternalId);
        Assert.AreEqual("Instance", response.SemanticKind);
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
        StringAssert.Contains(created.Location, "/api/graph/nodes?globalId=new%20node");

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
    public async Task CreateRelationAsync_ReturnsCanonicalSemanticRelation() {
        var relationType = (await Service.CreateEdgeType(
            "ApiShipment",
            fields: [
                new NodeFieldDefinition(
                    "Source",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false),
                new NodeFieldDefinition(
                    "Cargo",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Many(1),
                    IsCollection: true)
            ])).Value!.Type;
        var source = await Storage.Create(new("factory"));
        var firstCargo = await Storage.Create(new("rifle-1"));
        var secondCargo = await Storage.Create(new("rifle-2"));

        var result = await Controller.CreateRelationAsync(new CreateRelationRequest {
            LocalId = "shipment-1",
            TypeGlobalId = relationType.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Members = new Dictionary<string, string[][]>(StringComparer.Ordinal) {
                ["Source"] = [source.GlobalId.Select(static segment => segment.ToString()).ToArray()],
                ["Cargo"] = [
                    firstCargo.GlobalId.Select(static segment => segment.ToString()).ToArray(),
                    secondCargo.GlobalId.Select(static segment => segment.ToString()).ToArray()
                ]
            }
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("shipment-1", response.InternalId);
        Assert.AreEqual("Relation", response.SemanticKind);
        CollectionAssert.AreEqual(
            new[] { relationType.GlobalId.ToString() },
            response.TypeIds.ToArray());
        var members = response.RelationMembers.ToDictionary(static member => member.Name, StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            new[] { source.GlobalId.ToString() },
            members["Source"].ParticipantIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { firstCargo.GlobalId.ToString(), secondCargo.GlobalId.ToString() },
            members["Cargo"].ParticipantIds.ToArray());
        Assert.IsTrue(members.Values.All(static member => !string.IsNullOrWhiteSpace(member.MemberTypeId)));
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
        StringAssert.Contains(message, global::SemanticStorage.NodeNameValidator.AllowedSegmentCharactersDescription);
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
        StringAssert.Contains(message, "Address segment contains invalid character ':'");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_CanConnectInstancesWhoseIdsShareAddressPrefix() {
        var root = await Storage.Create(new("small_arms_test_graph"));
        await Storage.Create(new("weapons"), root.GlobalId);

        var result = await Controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = ["small_arms_test_graph"],
            Node2InternalId = ["small_arms_test_graph", "weapons"]
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        Assert.IsNotNull(await Storage.Get(new NodePath("small_arms_test_graph", "weapons")));
        Assert.AreEqual(1, ReadCanonicalRelationDocuments().Count);
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ConnectsNestedSiblingsThroughCanonicalRelationDocument() {
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

        var relation = ReadCanonicalRelationDocuments().Single();
        var participantIds = relation.GetProperty("members")
            .EnumerateArray()
            .SelectMany(static member => member.GetProperty("participantIds").EnumerateArray())
            .Select(static participant => participant.GetString())
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { weapons.GlobalId.ToString(), categories.GlobalId.ToString() },
            participantIds);

        Assert.IsTrue(await weapons.Nodes.AnyAsync(node => node.LocalId == categories.LocalId));
        Assert.IsTrue(await categories.Nodes.AnyAsync(node => node.LocalId == weapons.LocalId));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsBadRequestWhenConnectFails() {
        var source = await Storage.Create(new("source"));
        var target = await Storage.Create(new("target"));
        var controller = await CreateController(new ConnectThrowingGraphStorage(Storage));

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            Node1InternalId = [source.LocalId.ToString()],
            Node2InternalId = [target.LocalId.ToString()]
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "diagnostic connect failure");
    }

    [TestMethod]
    public async Task AssignNodeTypeAsync_AttachesTypeIdWithoutMaterializingCarrier() {
        var typeRoot = await GetTypesRoot();
        var weaponType = await Storage.Create(new("Weapon"), typeRoot.GlobalId);
        var ak47 = await Storage.Create(new("ak-47"));

        var result = await Controller.AssignNodeTypeAsync(new AssignNodeTypeRequest {
            InternalId = ak47.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = weaponType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        var returnedNode = response.Nodes.Single(node => node.InternalId == ak47.GlobalId.ToString());
        CollectionAssert.AreEqual(
            new[] { weaponType.GlobalId.ToString() },
            returnedNode.TypeIds.ToArray());

        var storedNode = await Storage.Get(ak47.GlobalId);
        Assert.IsNotNull(storedNode);
        Assert.IsFalse(storedNode.Attributes.ContainsKey("graph.typeName"));
        var instanceRecordPath = Path.Combine(
            StorageOptions.RootPath,
            StorageOptions.InstancesDirectoryName,
            "ak-47",
            "instance.json");
        using var instanceRecord = JsonDocument.Parse(await File.ReadAllTextAsync(instanceRecordPath, Encoding.UTF8));
        CollectionAssert.AreEqual(
            new[] { weaponType.GlobalId.ToString() },
            instanceRecord.RootElement.GetProperty("typeIds")
                .EnumerateArray()
                .Select(static typeId => typeId.GetString())
                .ToArray());
        Assert.AreEqual(1, Directory.GetFiles(
            Path.Combine(StorageOptions.RootPath, StorageOptions.InstancesDirectoryName),
            "instance.json",
            SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ChangeEdgeTypeAsync_PersistsSemanticRelationAndReturnsCompatibilityProjection() {
        var newType = (await Service.CreateEdgeType(
            new("new-type"),
            fields: [
                new NodeFieldDefinition(
                    "Source",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false),
                new NodeFieldDefinition(
                    "Target",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false)
            ])).Value!.Type;
        var source = await Storage.Create(new("source"));
        var target = await Storage.Create(new("target"));
        await Storage.Connect(source.GlobalId, target.GlobalId);

        var result = await Controller.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            Node1InternalId = source.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Node2InternalId = target.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = newType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);

        var relation = response.Nodes.Single(node => node.SemanticKind == "Relation");
        StringAssert.StartsWith(relation.InternalId, "new-type-");
        Assert.AreEqual(0, relation.Attributes.Count);
        Assert.AreEqual("Relation", relation.SemanticKind);
        CollectionAssert.AreEqual(
            new[] { newType.GlobalId.ToString() },
            relation.TypeIds.ToArray());
        var semanticMembers = relation.RelationMembers.ToDictionary(static member => member.Name, StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            new[] { source.GlobalId.ToString() },
            semanticMembers["Source"].ParticipantIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { target.GlobalId.ToString() },
            semanticMembers["Target"].ParticipantIds.ToArray());

        var returnedIds = response.Nodes.Select(static node => node.InternalId).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(returnedIds.Contains(source.GlobalId.ToString()));
        Assert.IsTrue(returnedIds.Contains(target.GlobalId.ToString()));
        Assert.IsTrue(returnedIds.Contains(newType.GlobalId.ToString()));

        var endpointPorts = response.Nodes
            .Where(node => GraphIdIsChildOf(node.InternalId, relation.InternalId) && node.InternalId != relation.InternalId)
            .ToArray();
        Assert.AreEqual(2, endpointPorts.Length);
        Assert.IsTrue(endpointPorts.All(endpoint =>
            endpoint.InternalId.StartsWith(
                $"{relation.InternalId}/@member-{relation.LocalId}-",
                StringComparison.Ordinal)));
        Assert.AreEqual(2, endpointPorts.Select(static endpoint => endpoint.LocalId).Distinct().Count());
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, relation.InternalId, newType.GlobalId.ToString())));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, source.GlobalId.ToString()))));
        Assert.IsTrue(endpointPorts.Any(port => response.Edges.Any(edge => HasEndpoints(edge, port.InternalId, target.GlobalId.ToString()))));

        var storedRelation = await Storage.Get(new NodePath(relation.InternalId));
        Assert.IsNotNull(storedRelation);
        Assert.AreEqual(0, storedRelation.Attributes.Count);

        var semanticEdge = await Service.GetTypedEdgeInstanceAsync(storedRelation.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, semanticEdge.Status, semanticEdge.Error);
        Assert.AreEqual(source.GlobalId, semanticEdge.Value!.Endpoint("Source").Participant.GlobalId);
        Assert.AreEqual(target.GlobalId, semanticEdge.Value.Endpoint("Target").Participant.GlobalId);

        var replacementType = (await Service.CreateEdgeType(
            new("replacement-type"),
            fields: [
                new NodeFieldDefinition(
                    "Left",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false),
                new NodeFieldDefinition(
                    "Right",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Required(),
                    IsCollection: false)
            ])).Value!.Type;
        await Storage.Update(storedRelation.GlobalId, new Dictionary<string, string> { ["note"] = "user note" });
        var retyped = await Controller.ChangeEdgeTypeAsync(new ChangeEdgeTypeRequest {
            Node1InternalId = source.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            Node2InternalId = target.GlobalId.Select(static segment => segment.ToString()).ToArray(),
            TypeGlobalId = replacementType.GlobalId.Select(static segment => segment.ToString()).ToArray()
        });
        var retypedOk = retyped.Result as OkObjectResult;
        Assert.IsNotNull(retypedOk);
        Assert.IsNull(await Storage.Get(storedRelation.GlobalId));
        var replacementResponse = retypedOk.Value as SubgraphResponse;
        Assert.IsNotNull(replacementResponse);
        var replacementId = replacementResponse.Nodes
            .Single(node => node.SemanticKind == "Relation")
            .InternalId;
        StringAssert.StartsWith(replacementId, "replacement-type-");
        var replacementRelation = await Storage.Get(new NodePath(replacementId));
        Assert.IsNotNull(replacementRelation);
        var replacementEdge = await Service.GetTypedEdgeInstanceAsync(replacementRelation.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, replacementEdge.Status, replacementEdge.Error);
        Assert.AreEqual(source.GlobalId, replacementEdge.Value!.Endpoint("Left").Participant.GlobalId);
        Assert.AreEqual(target.GlobalId, replacementEdge.Value.Endpoint("Right").Participant.GlobalId);
        Assert.IsFalse(await source.Nodes.AnyAsync(node => node.GlobalId == target.GlobalId));
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
    public async Task GetSubgraphAsync_ReturnsOnlyRelationNeighborsForNestedInstanceIds() {
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
            new[] { ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());

        Assert.AreEqual(1, response.Edges.Count);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));

        var ak47Node = response.Nodes.Single(node => node.InternalId == ak47.GlobalId.ToString());
        var assaultRifleNode = response.Nodes.Single(node => node.InternalId == assaultRifle.GlobalId.ToString());
        Assert.IsTrue(ak47Node.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
        Assert.IsTrue(assaultRifleNode.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_DoesNotTreatAddressPrefixesAsGraphEdges() {
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
        CollectionAssert.AreEqual(
            new[] { root.GlobalId.ToString() },
            response.Nodes.Select(static node => node.InternalId).ToArray());
        Assert.AreEqual(0, response.Edges.Count);
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
        Assert.IsFalse(response.Nodes.Any(node => node.InternalId == child.GlobalId.ToString()));
        Assert.AreEqual(0, firstNode.Edges.Count);
        Assert.AreEqual(0, secondNode.Edges.Count);
    }

    [TestMethod]
    public async Task GetTypeCatalogAsync_ReturnsDeepTypesWithoutScanningByGraphDepth() {
        var semantic = (ISemanticGraphStorage)Storage;
        NodeBacking parent = semantic.TypeRoot;
        for (var depth = 1; depth <= 7; depth++)
            parent = await semantic.CreateTypeElementAsync(
                new NodeLocalId($"Level{depth}"),
                parent.GlobalId,
                SemanticElementKind.Type,
                Array.Empty<NodeRef>());

        var instance = await semantic.CreateInstanceAsync(
            "unrelated-instance",
            null,
            Array.Empty<NodeRef>());

        var result = await Controller.GetTypeCatalogAsync(CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        Assert.IsTrue(response.Nodes.Any(node => node.InternalId == parent.GlobalId.ToString()));
        Assert.IsFalse(response.Nodes.Any(node => node.InternalId == instance.GlobalId.ToString()));
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
        GraphController Controller,
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

    private static IReadOnlyList<JsonElement> ReadCanonicalRelationDocuments() {
        var instancesPath = Path.Combine(
            StorageOptions.RootPath,
            StorageOptions.InstancesDirectoryName);
        if (!Directory.Exists(instancesPath))
            return [];

        var relations = new List<JsonElement>();
        foreach (var path in Directory.GetFiles(
            instancesPath,
            "instance.json",
            SearchOption.AllDirectories)) {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.TryGetProperty("kind", out var kind)
                && string.Equals(kind.GetString(), "relation", StringComparison.OrdinalIgnoreCase))
                relations.Add(root.Clone());
        }
        return relations;
    }

    private sealed class ConnectThrowingGraphStorage(IGraphStorage inner) : IGraphStorage, ISemanticGraphStorage {
        private ISemanticGraphStorage Semantic => (ISemanticGraphStorage)inner;

        public NodeBacking Root => inner.Root;
        public NodeBacking TypeRoot => Semantic.TypeRoot;
        public Task<NodeBacking> Create(NodeLocalId name, NodeRef? parent = null, IDictionary<string, string>? attributes = null) => inner.Create(name, parent, attributes);
        public Task<NodeBacking?> Get(NodeRef query) => inner.Get(query);
        public Task Delete(NodeRef query) => inner.Delete(query);
        public Task Connect(NodeRef sourcePath, NodeRef targetPath) => throw new InvalidOperationException("diagnostic connect failure");
        public Task Disconnect(NodeRef sourcePath, NodeRef targetPath) => throw new InvalidOperationException("diagnostic connect failure");
        public IAsyncEnumerable<NodeBacking> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] others) => inner.GetCommonIntersection(first, second, others);
        public Task Delete(NodeBacking node) => inner.Delete(node);
        public IAsyncEnumerable<NodeBacking> EnumerateNodesAsync(CancellationToken cancellationToken = default) => inner.EnumerateNodesAsync(cancellationToken);
        public bool IsType(NodeBacking node) => Semantic.IsType(node);
        public IReadOnlyCollection<InternalId> GetDeclaredTypeIds(NodeBacking node) => Semantic.GetDeclaredTypeIds(node);
        public IReadOnlyCollection<InternalId> GetRequiredTypeIds(NodeBacking type) => Semantic.GetRequiredTypeIds(type);
        public Task SetDeclaredTypeIdsAsync(NodeRef node, IEnumerable<NodeRef> typeIds) => Semantic.SetDeclaredTypeIdsAsync(node, typeIds);
        public Task SetRequiredTypeIdsAsync(NodeRef type, IEnumerable<NodeRef> requiredTypeIds) => Semantic.SetRequiredTypeIdsAsync(type, requiredTypeIds);
        public Task<NodeBacking> CreateTypeElementAsync(NodeLocalId localId, NodeRef parentId, SemanticElementKind kind, IReadOnlyCollection<NodeRef> requiredTypeIds, IDictionary<string, string>? attributes = null) =>
            Semantic.CreateTypeElementAsync(localId, parentId, kind, requiredTypeIds, attributes);
        public Task<NodeBacking> ReplaceTypeElementAsync(NodeRef typeId, SemanticElementKind expectedKind, IReadOnlyCollection<NodeRef> requiredTypeIds, IDictionary<string, string>? attributes = null) =>
            Semantic.ReplaceTypeElementAsync(typeId, expectedKind, requiredTypeIds, attributes);
        public Task<NodeBacking> CreateInstanceAsync(NodeLocalId localId, NodeRef? namespacePrefix, IReadOnlyCollection<NodeRef> typeIds, IDictionary<string, string>? attributes = null) =>
            Semantic.CreateInstanceAsync(localId, namespacePrefix, typeIds, attributes);
        public Task<NodeBacking> ReplaceInstanceAsync(NodeRef instanceId, IReadOnlyCollection<NodeRef> typeIds, IDictionary<string, string>? attributes = null) =>
            Semantic.ReplaceInstanceAsync(instanceId, typeIds, attributes);
        public Task<NodeBacking> CreateRelationAsync(NodeLocalId localId, IReadOnlyCollection<NodeRef> typeIds, IReadOnlyCollection<SemanticRelationMember> members, IDictionary<string, string>? attributes = null) =>
            Semantic.CreateRelationAsync(localId, typeIds, members, attributes);
        public Task<NodeBacking> ReplaceRelationAsync(NodeRef relationId, IReadOnlyCollection<NodeRef> typeIds, IReadOnlyCollection<SemanticRelationMember> members, IDictionary<string, string>? attributes = null) =>
            Semantic.ReplaceRelationAsync(relationId, typeIds, members, attributes);
        public Task<SemanticRelation?> GetRelationAsync(NodeRef relationId) => Semantic.GetRelationAsync(relationId);
        public Task<SemanticElementSnapshot?> ReadSemanticElementAsync(NodeRef elementId, CancellationToken cancellationToken = default) =>
            Semantic.ReadSemanticElementAsync(elementId, cancellationToken);
        public IAsyncEnumerable<SemanticRelation> FindIncidentRelationsAsync(NodeRef participantId, NodeRef? relationTypeId = null, string? memberName = null, CancellationToken cancellationToken = default) =>
            Semantic.FindIncidentRelationsAsync(participantId, relationTypeId, memberName, cancellationToken);
        public IAsyncEnumerable<NodeBacking> EnumerateSemanticElementsAsync(CancellationToken cancellationToken = default) =>
            Semantic.EnumerateSemanticElementsAsync(cancellationToken);
        public IAsyncEnumerable<NodeBacking> EnumerateTypeElementsAsync(CancellationToken cancellationToken = default) =>
            Semantic.EnumerateTypeElementsAsync(cancellationToken);
        public IAsyncEnumerable<NodeBacking> EnumerateInstanceNamespaceChildrenAsync(NodeRef? parent = null, CancellationToken cancellationToken = default) =>
            Semantic.EnumerateInstanceNamespaceChildrenAsync(parent, cancellationToken);
        public Task RepairDerivedIndexesAsync(CancellationToken cancellationToken = default) =>
            Semantic.RepairDerivedIndexesAsync(cancellationToken);
    }
}
