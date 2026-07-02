using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GraphData.Tests.GraphService.NodeTypes;

[RelevantTestClass]
public sealed class ServiceTests : GraphServiceTests {
    static Assembly[] todoDelete = [typeof(ManufacturedByConnectionNodeType).Assembly];
    protected override Assembly[] Assemblies => todoDelete;

    [TestMethod]
    public async Task LINQ_Traverse() {
        var typesRoot = await GetTypesRoot();
        var weaponTypeBaseNode = await Create("weapon", typesRoot.GlobalId)!;
        var weaponType = (await Service.GetTypeNode(weaponTypeBaseNode))!;
        var nodeResult = await Service.CreateNode("ak47", path: null, weaponType);
        Assert.AreEqual(ServiceResultStatus.Ok, nodeResult.Status, nodeResult.Error);
        var node = nodeResult.Value!;
        await Service.CreateNode("m16", path: null, weaponType);
        await Service.CreateNode("mp5", path: null, weaponType);

        var type = node.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Select(x => x.Type)
            .First();
        Assert.AreEqual<NodeLocalId>("weapon", type.LocalId);

        var metaType = type.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .FirstOrDefault();
        Assert.IsNull(metaType);

        var instances = type.Incidences
            .OfType<InstanceOf.TypeEnd>()
            .Select(x => x.Instance)
            .ToArray();
        CollectionAssert.AreEquivalent(new NodeLocalId[] { "ak47", "m16", "mp5" }, instances.Select(x => x.LocalId).ToArray());

        var storedNode = await Storage.Get(node.GlobalId);
        Assert.IsNotNull(storedNode);
        Assert.IsTrue(await storedNode.Nodes.AnyAsync(neighbor => neighbor.GlobalId == weaponType.GlobalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "ak47")));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, typesRoot.LocalId.ToString(), "weapon")));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldKeepExistingConnectionIdempotent() {
        var first = (await Service.CreateNode("existing-link-first")).Value!;
        var second = (await Service.CreateNode("existing-link-second")).Value!;

        var firstConnect = await Service.ConnectNodesAsync(first.GlobalId, second.GlobalId);
        var secondConnect = await Service.ConnectNodesAsync(first.GlobalId, second.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, firstConnect.Status, firstConnect.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, secondConnect.Status, secondConnect.Error);

        var storedFirst = await Storage.Get(first.GlobalId);
        var storedSecond = await Storage.Get(second.GlobalId);
        Assert.IsNotNull(storedFirst);
        Assert.IsNotNull(storedSecond);
        Assert.AreEqual(1, await storedFirst.Nodes.CountAsync(node => node.GlobalId == second.GlobalId));
        Assert.AreEqual(1, await storedSecond.Nodes.CountAsync(node => node.GlobalId == first.GlobalId));

        var firstLink = Path.Combine(StorageOptions.RootPath, first.LocalId.ToString(), second.LocalId.ToString());
        var secondLink = Path.Combine(StorageOptions.RootPath, second.LocalId.ToString(), first.LocalId.ToString());
        Assert.IsTrue(File.GetAttributes(firstLink).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(secondLink).HasFlag(FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public async Task Disconnect_ShouldKeepHierarchyChildByMovingItThroughRemainingEdge() {
        var oldParent = (await Service.CreateNode("old-parent")).Value!;
        var child = (await Service.CreateNode("child", oldParent.GlobalId)).Value!;
        var newParent = (await Service.CreateNode("new-parent")).Value!;
        var result = await Service.ConnectNodesAsync(child.GlobalId, newParent.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status);

        result = await Service.Disconnect(oldParent.GlobalId, child.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status);

        var oldChild = await Service.GetNode(new NodePath("old-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, oldChild.Status);

        var movedChild = await Service.GetNode(new NodePath("new-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.Ok, movedChild.Status);
        Assert.IsNotNull(movedChild.Value);
        Assert.AreEqual<NodeLocalId>("child", movedChild.Value.LocalId);

        var reloadedOldParent = await Service.GetNode(new NodePath("old-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedOldParent.Status);
        Assert.IsNotNull(reloadedOldParent.Value);
        Assert.IsFalse(reloadedOldParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var reloadedNewParent = await Service.GetNode(new NodePath("new-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));

        Assert.IsNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
        Assert.IsNotNull(await Storage.Get(newParent.GlobalId, child.LocalId));
        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "old-parent", "child")));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "new-parent", "child")));
    }

    [TestMethod]
    public async Task DeleteNode_ShouldDeleteHierarchyChild() {
        var parent = await Create("parent");
        var child = await Create("child", parent.GlobalId);

        var result = await Service.DeleteNode(child.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);

        var reloadedParent = await Service.GetNode(new NodePath("parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedParent.Status);
        Assert.IsNotNull(reloadedParent.Value);
        Assert.IsFalse(reloadedParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var deletedChild = await Service.GetNode(new NodePath("parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, deletedChild.Status);
        Assert.IsNull(await Storage.Get(parent.GlobalId, child.LocalId));
        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "parent", "child")));
    }

    [TestMethod]
    public async Task DeleteNode_ShouldRejectStorageRoot() {
        var node = await Create("node");

        var result = await Service.DeleteNode(new NodePath());

        Assert.AreEqual(ServiceResultStatus.BadRequest, result.Status);
        Assert.AreEqual("Storage root cannot be deleted.", result.Error);
        Assert.IsTrue(Directory.Exists(StorageOptions.RootPath));
        Assert.IsNotNull(await Storage.Get(node.GlobalId));
    }

    [TestMethod]
    public async Task GraphService_ChangeEdgeTypeAsync_CreatesMetadataFreeTypedEdgeSubgraph() {
        var typesRoot = await GetTypesRoot();
        var weapon = await Service.CreateNode<EdgeWeaponNodeType>(new("ak-47"));
        Assert.AreEqual(ServiceResultStatus.Ok, weapon.Status, weapon.Error);
        var manufacturer = await Service.CreateNode<EdgeManufacturerNodeType>(new("kalashnikov"));
        Assert.AreEqual(ServiceResultStatus.Ok, manufacturer.Status, manufacturer.Error);
        await Service.ConnectNodesAsync(weapon.Value!.GlobalId, manufacturer.Value!.GlobalId);

        var result = await Service.ChangeEdgeTypeAsync<ManufacturedByConnectionNodeType>(
            weapon.Value.GlobalId,
            manufacturer.Value.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);

        var relationId = new NodePath("manufactured-by-1");
        var relation = await Storage.Get(relationId);
        Assert.IsNotNull(relation);
        AssertMetadataFree(relation);

        var weaponEndpointId = new NodePath("manufactured-by-1", "endpoint-Weapon");
        var manufacturerEndpointId = new NodePath("manufactured-by-1", "endpoint-Manufacturer");
        var weaponEndpointSpecId = new NodePath("manufactured-by-1", "endpoint-Weapon", "endpoint-spec-Weapon");
        var manufacturerEndpointSpecId = new NodePath("manufactured-by-1", "endpoint-Manufacturer", "endpoint-spec-Manufacturer");

        var weaponEndpoint = await Storage.Get(weaponEndpointId);
        var manufacturerEndpoint = await Storage.Get(manufacturerEndpointId);
        var weaponEndpointSpec = await Storage.Get(weaponEndpointSpecId);
        var manufacturerEndpointSpec = await Storage.Get(manufacturerEndpointSpecId);
        Assert.IsNotNull(weaponEndpoint);
        Assert.IsNotNull(manufacturerEndpoint);
        Assert.IsNotNull(weaponEndpointSpec);
        Assert.IsNotNull(manufacturerEndpointSpec);
        AssertMetadataFree(weaponEndpoint);
        AssertMetadataFree(manufacturerEndpoint);
        AssertMetadataFree(weaponEndpointSpec);
        AssertMetadataFree(manufacturerEndpointSpec);

        var edgeTypeId = await GetTypedEdgeTypeIdAsync<ManufacturedByConnectionNodeType>(Service);
        var endpointTypeId = await GetNodeTypeIdAsync<EndpointNodeType>(Service);
        var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(Service);
        var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(Service);

        Assert.IsTrue(await relation.Nodes.AnyAsync(node => node.GlobalId == edgeTypeId));
        var edgeType = await Storage.Get(edgeTypeId);
        Assert.IsNotNull(edgeType);
        Assert.IsTrue(await edgeType.Nodes.AnyAsync(node => node.GlobalId == typesRoot.GlobalId));
        Assert.IsTrue(await weaponEndpoint.Nodes.AnyAsync(node => node.GlobalId == endpointTypeId));
        Assert.IsTrue(await manufacturerEndpoint.Nodes.AnyAsync(node => node.GlobalId == endpointTypeId));
        Assert.IsTrue(await weaponEndpoint.Nodes.AnyAsync(node => node.GlobalId == weapon.Value!.GlobalId));
        Assert.IsTrue(await manufacturerEndpoint.Nodes.AnyAsync(node => node.GlobalId == manufacturer.Value!.GlobalId));
        Assert.IsTrue(await weaponEndpoint.Nodes.AnyAsync(node => node.GlobalId == weaponEndpointSpec.GlobalId));
        Assert.IsTrue(await manufacturerEndpoint.Nodes.AnyAsync(node => node.GlobalId == manufacturerEndpointSpec.GlobalId));
        Assert.IsTrue(await weaponEndpointSpec.Nodes.AnyAsync(node => node.GlobalId == weaponNodeTypeId));
        Assert.IsTrue(await manufacturerEndpointSpec.Nodes.AnyAsync(node => node.GlobalId == manufacturerNodeTypeId));

        var storedWeapon = await Storage.Get(weapon.Value.GlobalId);
        Assert.IsNotNull(storedWeapon);
        Assert.IsFalse(await storedWeapon.Nodes.AnyAsync(node => node.GlobalId == manufacturer.Value.GlobalId));

        var relationPath = Path.Combine(StorageOptions.RootPath, "manufactured-by-1");
        Assert.IsTrue(Directory.Exists(relationPath));
        Assert.IsTrue(Directory.Exists(Path.Combine(relationPath, "endpoint-Weapon")));
        Assert.IsTrue(Directory.Exists(Path.Combine(relationPath, "endpoint-Manufacturer")));
        Assert.IsFalse(File.Exists(Path.Combine(relationPath, StorageOptions.MetadataFileName)));

        var returnedIds = result.Value!.Nodes.Select(static node => node.GlobalId.ToString()).ToArray();
        CollectionAssert.IsSubsetOf(
            new[] {
                relationId.ToString(),
                edgeTypeId.ToString(),
                weapon.Value.GlobalId.ToString(),
                manufacturer.Value.GlobalId.ToString(),
                weaponEndpointId.ToString(),
                manufacturerEndpointId.ToString(),
                weaponEndpointSpec.GlobalId.ToString(),
                manufacturerEndpointSpec.GlobalId.ToString()
            },
            returnedIds);
    }

    [TestMethod]
    public async Task GraphService_GetTypedEdgeDefinitionAsync_DiscoversRichEndpointMembers() {
        var result = await Service.GetTypedEdgeDefinitionAsync<ShipmentConnectionNodeType>();

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var definition = result.Value!;
        var typeState = await Storage.Get(definition.Type.GlobalId);
        Assert.IsNotNull(typeState);
        AssertMetadataFree(typeState);
        var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(Service);
        var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(Service);

        Assert.AreEqual(4, definition.Endpoints.Count);
        AssertEndpoint(definition, nameof(ShipmentConnectionNodeType.Weapon), typeof(EdgeWeaponNodeType), NodeSlotCardinality.Required(), weaponNodeTypeId);
        AssertEndpoint(definition, nameof(ShipmentConnectionNodeType.Counterparty), typeof(Node), NodeSlotCardinality.Required());
        AssertEndpoint(definition, nameof(ShipmentConnectionNodeType.OptionalWaypoint), typeof(Node), NodeSlotCardinality.Optional());
        AssertEndpoint(definition, nameof(ShipmentConnectionNodeType.Manufacturers), typeof(EdgeManufacturerNodeType), NodeSlotCardinality.Many(), manufacturerNodeTypeId, isCollection: true);
        Assert.IsFalse(definition.Endpoints.Any(endpoint => endpoint.Name == nameof(ShipmentConnectionNodeType.Note)));
    }

    [TestMethod]
    public async Task GraphService_AssignNodeTypeAsync_UsesRegisteredNodeTypeDescriptor() {
        var ak47Result = await Service.CreateNode(new NodeLocalId("ak-47"));
        Assert.AreEqual(ServiceResultStatus.Ok, ak47Result.Status, ak47Result.Error);
        var ak47 = ak47Result.Value!;

        var invalid = await Service.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "Manufacturer");
        var weaponTypeId = await GetNodeTypeIdAsync<WeaponNodeType>(Service);
        var storedAk47 = await Storage.Get(ak47.GlobalId);
        Assert.IsNotNull(storedAk47);
        Assert.IsFalse(await storedAk47.Nodes.AnyAsync(node => node.GlobalId == weaponTypeId));

        var country = await Service.CreateNode<CountryNodeType>(new("ussr"));
        Assert.AreEqual(ServiceResultStatus.Ok, country.Status, country.Error);
        var manufacturerResult = await Service.CreateNode(new NodeLocalId("kalashnikov"), attributes: new Dictionary<string, string> {
            [nameof(ManufacturerNodeType.LegalName)] = "Kalashnikov Concern",
            [nameof(ManufacturerNodeType.FoundedYear)] = "1807",
            [nameof(ManufacturerNodeType.IsActive)] = "true"
        });
        Assert.AreEqual(ServiceResultStatus.Ok, manufacturerResult.Status, manufacturerResult.Error);
        var manufacturer = manufacturerResult.Value!;
        var connectCountry = await Service.ConnectNodesAsync(manufacturer.GlobalId, country.Value!.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, connectCountry.Status, connectCountry.Error);
        var assignManufacturer = await Service.AssignNodeTypeAsync<ManufacturerNodeType>(manufacturer.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, assignManufacturer.Status, assignManufacturer.Error);
        var connectManufacturer = await Service.ConnectNodesAsync(ak47.GlobalId, manufacturer.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, connectManufacturer.Status, connectManufacturer.Error);

        var valid = await Service.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        storedAk47 = await Storage.Get(ak47.GlobalId);
        Assert.IsNotNull(storedAk47);
        Assert.IsTrue(await storedAk47.Nodes.AnyAsync(node => node.GlobalId == weaponTypeId));
        Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "ak-47", "Weapon")).HasFlag(FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public async Task NodeTypeDefinition_DiscoversRichCSharpFields() {
        var result = await Service.GetNodeTypeDefinitionAsync<ManufacturerNodeType>();

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var definition = result.Value!;
        var countryTypeId = await GetNodeTypeIdAsync<CountryNodeType>(Service);
        var manufacturerTypeId = await GetNodeTypeIdAsync<ManufacturerNodeType>(Service);
        var weaponTypeId = await GetNodeTypeIdAsync<WeaponNodeType>(Service);

        AssertField(definition, nameof(ManufacturerNodeType.Country), NodeFieldValueKind.Node, typeof(CountryNodeType), NodeSlotCardinality.Required(), countryTypeId);
        AssertField(definition, nameof(ManufacturerNodeType.ParentCompany), NodeFieldValueKind.Node, typeof(ManufacturerNodeType), NodeSlotCardinality.Optional(), manufacturerTypeId);
        AssertField(definition, nameof(ManufacturerNodeType.ProducedWeapons), NodeFieldValueKind.Node, typeof(WeaponNodeType), NodeSlotCardinality.Many(), weaponTypeId, isCollection: true);
        AssertField(definition, nameof(ManufacturerNodeType.Headquarters), NodeFieldValueKind.Node, typeof(Node), NodeSlotCardinality.Required());
        AssertField(definition, nameof(ManufacturerNodeType.ArchiveNode), NodeFieldValueKind.Node, typeof(Node), NodeSlotCardinality.Optional());
        AssertField(definition, nameof(ManufacturerNodeType.LegalName), NodeFieldValueKind.Primitive, typeof(string), NodeSlotCardinality.Required());
        AssertField(definition, nameof(ManufacturerNodeType.FoundedYear), NodeFieldValueKind.Primitive, typeof(int), NodeSlotCardinality.Required());
        AssertField(definition, nameof(ManufacturerNodeType.IsActive), NodeFieldValueKind.Primitive, typeof(bool), NodeSlotCardinality.Required());
        AssertField(definition, nameof(ManufacturerNodeType.AnnualRevenueUsd), NodeFieldValueKind.Primitive, typeof(decimal?), NodeSlotCardinality.Optional());
        AssertField(definition, nameof(ManufacturerNodeType.Website), NodeFieldValueKind.Primitive, typeof(string), NodeSlotCardinality.Optional());
        AssertField(definition, nameof(ManufacturerNodeType.Aliases), NodeFieldValueKind.Primitive, typeof(string), NodeSlotCardinality.Many(), isCollection: true);

        CollectionAssert.IsSubsetOf(
            new[] {
                nameof(ManufacturerNodeType.Country),
                nameof(ManufacturerNodeType.ParentCompany),
                nameof(ManufacturerNodeType.ProducedWeapons)
            },
            definition.Slots.Select(static slot => slot.Name).ToArray());

        void AssertField(NodeTypeDefinition d, string field, NodeFieldValueKind kind, Type type, NodeSlotCardinality car, NodeRef? id = null, bool isCollection = false) {
            var actual = d.Fields.Single(value => value.Name == field);
            Assert.AreEqual(kind, actual.ValueKind);
            Assert.AreEqual(type, actual.ClrType);
            Assert.AreEqual(car, actual.Cardinality);
            Assert.AreEqual(isCollection, actual.IsCollection);
            Assert.AreEqual(id, actual.NodeType?.GlobalId);
        }
    }

    [TestMethod]
    public async Task GraphService_AssignNodeTypeAsync_UsesGraphTypeTopologyInsteadOfInternalIdShape() {
        var typeRoot = await GetTypesRoot();
        var connectedTypeResult = await Service.CreateNode(new NodeLocalId("weapon-type"), typeRoot.GlobalId);
        var unrelatedNodeResult = await Service.CreateNode(new NodeLocalId("Fake"));
        var ak47Result = await Service.CreateNode(new NodeLocalId("ak-47"));
        var m16Result = await Service.CreateNode(new NodeLocalId("m16"));
        var fnFalResult = await Service.CreateNode(new NodeLocalId("fn-fal"));
        Assert.AreEqual(ServiceResultStatus.Ok, connectedTypeResult.Status, connectedTypeResult.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, unrelatedNodeResult.Status, unrelatedNodeResult.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, ak47Result.Status, ak47Result.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, m16Result.Status, m16Result.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, fnFalResult.Status, fnFalResult.Error);
        var connectedType = connectedTypeResult.Value!;
        var unrelatedNode = unrelatedNodeResult.Value!;
        var ak47 = ak47Result.Value!;
        var m16 = m16Result.Value!;
        var fnFal = fnFalResult.Value!;

        var connectedResult = await Service.AssignNodeTypeAsync(ak47.GlobalId, connectedType.GlobalId);
        var unrelatedResult = await Service.AssignNodeTypeAsync(m16.GlobalId, unrelatedNode.GlobalId);
        var rootResult = await Service.AssignNodeTypeAsync(fnFal.GlobalId, typeRoot.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, connectedResult.Status, connectedResult.Error);
        Assert.AreEqual(ServiceResultStatus.BadRequest, unrelatedResult.Status);
        StringAssert.Contains(unrelatedResult.Error, "not a node type");
        Assert.AreEqual(ServiceResultStatus.BadRequest, rootResult.Status);
        StringAssert.Contains(rootResult.Error, "not a node type");

        var storedAk47 = await Storage.Get(ak47.GlobalId);
        var storedM16 = await Storage.Get(m16.GlobalId);
        var storedFnFal = await Storage.Get(fnFal.GlobalId);
        Assert.IsNotNull(storedAk47);
        Assert.IsNotNull(storedM16);
        Assert.IsNotNull(storedFnFal);
        Assert.IsTrue(await storedAk47.Nodes.AnyAsync(node => node.GlobalId == connectedType.GlobalId));
        Assert.IsFalse(await storedM16.Nodes.AnyAsync(node => node.GlobalId == unrelatedNode.GlobalId));
        Assert.IsFalse(await storedFnFal.Nodes.AnyAsync(node => node.GlobalId == typeRoot.GlobalId));
    }

    [TestMethod]
    public async Task ShouldRespectDepth() {
        var first = await Create("first");
        var second = await Create("second");
        var third = await Create("third");
        var fourth = await Create("fourth");

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.ConnectNodesAsync(first.GlobalId, second.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.ConnectNodesAsync(second.GlobalId, third.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.ConnectNodesAsync(third.GlobalId, fourth.GlobalId)).Status);

        var subgraph = (await Service.GetSubgraph([first.GlobalId], 2)).Value!;
        var subgraphNodeIds = subgraph.Nodes.Select(static node => node.GlobalId).ToArray();

        Assert.AreEqual(3, subgraph.Nodes.Count);
        Assert.IsTrue(subgraphNodeIds.Contains(first.GlobalId));
        Assert.IsTrue(subgraphNodeIds.Contains(second.GlobalId));
        Assert.IsTrue(subgraphNodeIds.Contains(third.GlobalId));
        Assert.IsFalse(subgraphNodeIds.Contains(fourth.GlobalId));

        var children = subgraph.Nodes
            .First(x => x.LocalId == first.LocalId)
            .Edges
            .SelectMany(x => new[] { x.Node1, x.Node2 })
            .DistinctBy(static node => node.GlobalId)
            .Where(node => node.GlobalId != first.GlobalId)
            .ToArray();
        Assert.IsTrue(children.Any(x => x.LocalId == second.LocalId));
        Assert.IsFalse(children.Any(x => x.LocalId == third.LocalId));

        Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "first", "second")).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "second", "third")).HasFlag(FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public async Task ShouldTraverseHierarchy() {
        var root = await Create("root");
        var weapons = await Create("weapons", root.GlobalId);
        var ak47 = await Create("ak_47", weapons.GlobalId);

        var subgraph = (await Service.GetSubgraph([root.GlobalId], 2)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { root.GlobalId, weapons.GlobalId, ak47.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "root", "weapons", "ak_47")));
    }

    [TestMethod]
    public async Task EmptyQueryShouldStartFromTopLevelRoots() {
        var firstRoot = await Create("first");
        var secondRoot = await Create("second");
        var child = await Create("child", firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId, Graph.NodeTypes.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
        CollectionAssert.DoesNotContain(subgraph.Nodes.Select(static node => node.GlobalId).ToArray(), child.GlobalId);
        Assert.IsNotNull(await Storage.Get(child.GlobalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "first", "child")));
    }

    [TestMethod]
    public async Task EmptyNodeIdentifierShouldStartFromTopLevelRoots() {
        var firstRoot = await Create("first");
        var secondRoot = await Create("second");
        await Create("child", firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([new NodePath()], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId, Graph.NodeTypes.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "first", "child")));
    }

    [TestMethod]
    public async Task NodesRemove_ShouldKeepHierarchyChildWhenItHasOtherEdges() {
        var oldParent = await Create("old");
        var child = await Create("child", oldParent.GlobalId);
        var newParent = await Create("new");
        var result = await Service.ConnectNodesAsync(newParent.GlobalId, child.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status);

        result = await Service.Disconnect(oldParent.GlobalId, child.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status);

        var oldChild = await Service.GetNode(new NodePath(oldParent.LocalId, child.LocalId));
        Assert.AreEqual(ServiceResultStatus.NotFound, oldChild.Status);

        var movedChild = await Service.GetNode(new NodePath(newParent.LocalId, child.LocalId));
        Assert.AreEqual(ServiceResultStatus.Ok, movedChild.Status);
        Assert.IsNotNull(movedChild.Value);
        Assert.AreEqual<NodeLocalId>("child", movedChild.Value.LocalId);

        var reloadedOldParent = await Service.GetNode(oldParent.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedOldParent.Status);
        Assert.IsNotNull(reloadedOldParent.Value);
        Assert.IsFalse(reloadedOldParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var reloadedNewParent = await Service.GetNode(newParent.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));

        Assert.IsNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "old", "child")));

        Assert.IsNotNull(await Storage.Get(newParent.GlobalId, child.LocalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "new", "child")));
    }

    [TestMethod]
    public async Task EdgesRemove_ShouldThrowWhenHierarchyEdgeIsChildsOnlyEdge() {
        var parent = await Create("parent");
        var child = await Create("child", parent.GlobalId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Service.Disconnect(parent.GlobalId, child.GlobalId));
        Assert.IsNotNull(await Storage.Get(child.GlobalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "parent", "child")));
    }

    private static async Task<InternalId> GetTypedEdgeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService Service)
        where TNodeType : NodeType {
        var result = await Service.GetTypedEdgeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.Type.GlobalId;
    }

    private static async Task<InternalId> GetNodeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService Service)
        where TNodeType : NodeType {
        var result = await Service.GetNodeTypeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.Type.GlobalId;
    }

    private static void AssertMetadataFree(NodeBacking node) {
        Assert.AreEqual(0, node.Attributes.Count);
        Assert.IsFalse(node.Attributes.ContainsKey("Service.kind"));
        Assert.IsFalse(node.Attributes.ContainsKey("Service.role"));
        Assert.IsFalse(node.Attributes.ContainsKey("Service.typeName"));
    }

    private static void AssertEndpoint(
        TypedEdgeDefinition definition,
        string name,
        Type clrType,
        NodeSlotCardinality cardinality,
        InternalId? nodeTypeId = null,
        bool isCollection = false) {
        var endpoint = definition.Endpoints.Single(value => value.Name == name);
        Assert.AreEqual(clrType, endpoint.ClrType);
        Assert.AreEqual(cardinality, endpoint.Cardinality);
        Assert.AreEqual(nodeTypeId, endpoint.NodeType?.GlobalId);
        Assert.AreEqual(isCollection, endpoint.IsCollection);
    }
}
