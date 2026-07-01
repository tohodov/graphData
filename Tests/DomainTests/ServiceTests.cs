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
        var typesRoot = (await Service.GetSubgraph([], 0)).Value!.Nodes.Single();
        var weaponTypeBaseNode = await Create("weapon", typesRoot.GlobalId)!;
        var weaponType = (await Service.GetTypeNode(weaponTypeBaseNode))!;
        var node = ((Node)await Service.CreateNode("ak47", path: null, weaponType))!;
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
    }

    [TestMethod]
    public async Task NodesAdd_ShouldThrowWhenExistingNodeIsAlreadyLinked() {
        var first = (await Service.CreateNode("existing-link-first")).Value!;
        var second = (await Service.CreateNode("existing-link-second")).Value!;
        first.Nodes.Add(second);

        Assert.ThrowsException<InvalidOperationException>(() => first.Nodes.Add(second));
    }
    [TestMethod]
    public async Task NodeNodes_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = (await Service.CreateNode("dsl-nodes-parent")).Value!;
        var first = (await Service.CreateNode("first", parent.GlobalId)).Value!;
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Nodes.Select(static child => child.LocalId).ToArray());

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            parent.Nodes.Select(static child => child.LocalId).ToArray());

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Nodes.Select(static child => child.LocalId).ToArray());
    }
    [TestMethod]
    public async Task NodeEdges_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = (await Service.CreateNode("dsl-edges-parent")).Value!;
        var first = (await Service.CreateNode("first", parent.GlobalId)).Value!;
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());
    }
    [TestMethod]
    public async Task NodeIncidences_ShouldReflectDslTypeAttachmentsAfterFirstRead() {
        var typeRoot = await GetTypesRoot();
        var weaponTypeBaseNode = (await Service.CreateNode("dsl-incidence-weapon", typeRoot.GlobalId)).Value!;
        var weaponType = (await Service.GetTypeNode(weaponTypeBaseNode))!;

        CollectionAssert.AreEquivalent(
            Array.Empty<NodeLocalId>(),
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());

        var ak47 = (await Service.CreateNode("dsl-incidence-ak47", path: null, weaponType)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId },
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());

        var m16 = (await Service.CreateNode("dsl-incidence-m16", path: null, weaponType)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId, m16.LocalId },
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());
    }
    [TestMethod]
    public async Task EdgesRemove_ShouldKeepHierarchyChildByMovingItThroughRemainingEdge() {
        var oldParent = (await Service.CreateNode("old-parent")).Value!;
        var child = (await Service.CreateNode("child", oldParent.GlobalId)).Value!;
        var newParent = (await Service.CreateNode("new-parent")).Value!;
        var result = await Service.ConnectNodesAsync(child.GlobalId, newParent.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status);
        var hierarchyEdge = oldParent.Edges.Single();

        oldParent.Edges.Remove(hierarchyEdge);

        var oldChild = await Service.GetNode(new InternalId("old-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, oldChild.Status);

        var movedChild = await Service.GetNode(new InternalId("new-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.Ok, movedChild.Status);
        Assert.IsNotNull(movedChild.Value);
        Assert.AreEqual<NodeLocalId>("child", movedChild.Value.LocalId);

        var reloadedOldParent = await Service.GetNode(new InternalId("old-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedOldParent.Status);
        Assert.IsNotNull(reloadedOldParent.Value);
        Assert.IsFalse(reloadedOldParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var reloadedNewParent = await Service.GetNode(new InternalId("new-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));
    }
    [TestMethod]
    public async Task NodesRemove_ShouldDeleteHierarchyChild() {
        var parent = await Create("parent");
        var child = await Create("child", parent.GlobalId);

        parent.Nodes.Remove(child);

        var reloadedParent = await Service.GetNode(new InternalId("parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedParent.Status);
        Assert.IsNotNull(reloadedParent.Value);
        Assert.IsFalse(reloadedParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var deletedChild = await Service.GetNode(new InternalId("parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, deletedChild.Status);
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

        throw new NotImplementedException();
        //var relationId = new InternalId("manufactured-by-1");
        //var relation = await Storage.Get(relationId);
        //Assert.IsNotNull(relation);
        //AssertMetadataFree(relation);

        //var weaponEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByConnectionNodeType.Weapon)));
        //var manufacturerEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByConnectionNodeType.Manufacturer)));
        //var weaponEndpoint = await Storage.Get(weaponEndpointId);
        //Assert.IsNotNull(weaponEndpoint);
        //var manufacturerEndpoint = await Storage.Get(manufacturerEndpointId);
        //Assert.IsNotNull(manufacturerEndpoint);
        //AssertMetadataFree(weaponEndpoint);
        //AssertMetadataFree(manufacturerEndpoint);

        //var edgeTypeId = await GetTypedEdgeTypeIdAsync<ManufacturedByConnectionNodeType>(Service);
        //var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(Service);
        //var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(Service);

        //var weaponEndpointSpec = await GetEndpointSpecAsync(weaponEndpointId, relationId, weapon.Value.GlobalId);
        //var manufacturerEndpointSpec = await GetEndpointSpecAsync(manufacturerEndpointId, relationId, manufacturer.Value.GlobalId);

        //Assert.IsTrue(await relation.Nodes.AnyAsync(node => node.GlobalId == edgeTypeId));
        //var edgeType = await Storage.Get(edgeTypeId);
        //Assert.IsTrue(await edgeType!.Nodes.AnyAsync(node => node.GlobalId == typesRoot.GlobalId));
        //await AssertConnected(weaponEndpointId, typesRoot.GlobalId);
        //await AssertConnected(manufacturerEndpointId, typesRoot.GlobalId);
        //await AssertConnected(weaponEndpointId, weapon.Value.GlobalId);
        //await AssertConnected(weaponEndpointId, weaponEndpointSpec.GlobalId);
        //await AssertConnected(manufacturerEndpointId, manufacturer.Value.GlobalId);
        //await AssertConnected(manufacturerEndpointId, manufacturerEndpointSpec.GlobalId);
        //await AssertConnected(weaponEndpointSpec.GlobalId, weaponNodeTypeId);
        //await AssertConnected(manufacturerEndpointSpec.GlobalId, manufacturerNodeTypeId);

        //var weaponConnections = await Storage.GetNeighbors(weapon.Value.GlobalId).ToArrayAsync();
        //Assert.IsFalse(weaponConnections.Any(node => node.GlobalId == manufacturer.Value.GlobalId));

        //var returnedIds = result.Value!.Nodes.Select(static node => node.GlobalId).ToArray();
        //CollectionAssert.IsSubsetOf(
        //    new[] {
        //        relationId,
        //        edgeTypeId,
        //        weapon.Value.GlobalId,
        //        manufacturer.Value.GlobalId,
        //        weaponEndpointId,
        //        manufacturerEndpointId,
        //        weaponEndpointSpec.GlobalId,
        //        manufacturerEndpointSpec.GlobalId
        //    },
        //    returnedIds);
    }

    [TestMethod]
    public async Task GraphService_GetTypedEdgeDefinitionAsync_DiscoversRichEndpointMembers() {
        var result = await Service.GetTypedEdgeDefinitionAsync<ShipmentConnectionNodeType>();

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var definition = result.Value!;
        var typeState = await Storage.Get(definition.TypeId);
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
        var ak47 = await Storage.Create(new("ak-47"));

        var invalid = await Service.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "Manufacturer");
        var weaponTypeId = await GetNodeTypeIdAsync<WeaponNodeType>(Service);
        Assert.IsFalse(await ak47.Nodes.AnyAsync(node => node.GlobalId == weaponTypeId));

        var country = await Service.CreateNode<CountryNodeType>(new("ussr"));
        Assert.AreEqual(ServiceResultStatus.Ok, country.Status, country.Error);
        var manufacturer = await Storage.Create(new("kalashnikov"), attributes: new Dictionary<string, string> {
            [nameof(ManufacturerNodeType.LegalName)] = "Kalashnikov Concern",
            [nameof(ManufacturerNodeType.FoundedYear)] = "1807",
            [nameof(ManufacturerNodeType.IsActive)] = "true"
        });
        await Service.ConnectNodesAsync(manufacturer.GlobalId, country.Value!.GlobalId);
        var assignManufacturer = await Service.AssignNodeTypeAsync<ManufacturerNodeType>(manufacturer.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, assignManufacturer.Status, assignManufacturer.Error);
        await Service.ConnectNodesAsync(ak47.GlobalId, manufacturer.GlobalId);

        var valid = await Service.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        Assert.IsTrue(await ak47.Nodes.AnyAsync(node => node.GlobalId == weaponTypeId));
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
            throw new NotImplementedException();
        }
    }

    [TestMethod]
    public async Task NodeTypeDefinition_EnforcesRequiredTypedSlot() {
        var typeRoot = await GetTypesRoot();
        var weaponTypeState = await Storage.Create("weapon-type", typeRoot.GlobalId);
        var manufacturerTypeState = await Storage.Create("manufacturer-type", typeRoot.GlobalId);
        var weaponType = await Service.GetTypeNode(weaponTypeState.GlobalId)!;
        var manufacturerType = await Service.GetTypeNode(manufacturerTypeState.GlobalId)!;
        var builder = new NodeTypeBuilder(weaponType!, type => throw new NotImplementedException());
        var definition = builder.RequiresSlot("manufacturer", manufacturerType!).Build();
        var ak47 = await Storage.Create(new("ak-47"));
        await Storage.Connect(ak47.GlobalId, weaponType!.GlobalId);

        var invalid = new InstanceNode(ak47, weaponType);

        Assert.ThrowsException<InvalidOperationException>(() => definition.EnsureSatisfiedBy(invalid));

        var manufacturer = await Storage.Create(new("kalashnikov"));
        await Storage.Connect(manufacturer.GlobalId, manufacturerType!.GlobalId);
        await Storage.Connect(ak47.GlobalId, manufacturer.GlobalId);
        var valid = new InstanceNode(ak47, weaponType);

        definition.EnsureSatisfiedBy(valid);
    }

    [TestMethod]
    public async Task GraphService_AssignNodeTypeAsync_UsesGraphTypeTopologyInsteadOfInternalIdShape() {
        var typeRoot = await GetTypesRoot();
        var connectedType = await Storage.Create(new("weapon-type"), typeRoot.GlobalId);
        var unrelatedNode = await Storage.Create(new("Fake"));
        var ak47 = await Storage.Create(new("ak-47"));
        var m16 = await Storage.Create(new("m16"));
        var fnFal = await Storage.Create(new("fn-fal"));

        var connectedResult = await Service.AssignNodeTypeAsync(ak47.GlobalId, connectedType.GlobalId);
        var unrelatedResult = await Service.AssignNodeTypeAsync(m16.GlobalId, unrelatedNode.GlobalId);
        var rootResult = await Service.AssignNodeTypeAsync(fnFal.GlobalId, typeRoot.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, connectedResult.Status, connectedResult.Error);
        Assert.AreEqual(ServiceResultStatus.BadRequest, unrelatedResult.Status);
        StringAssert.Contains(unrelatedResult.Error, "not a node type");
        Assert.AreEqual(ServiceResultStatus.BadRequest, rootResult.Status);
        StringAssert.Contains(rootResult.Error, "not a node type");
    }

    [TestMethod]
    public async Task ShouldRespectDepth() {
        var first = await Storage.Create("first");
        var second = await Storage.Create("second");
        var third = await Storage.Create("third");
        var fourth = await Storage.Create("fourth");

        await Storage.Connect(first.GlobalId, second.GlobalId);
        await Storage.Connect(second.GlobalId, third.GlobalId);
        await Storage.Connect(third.GlobalId, fourth.GlobalId);

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
    }

    [TestMethod]
    public async Task ShouldTraverseHierarchy() {
        var root = await Storage.Create(new("root"));
        var weapons = await Storage.Create(new("weapons"), root.GlobalId);
        var ak47 = await Storage.Create(new("ak_47"), weapons.GlobalId);

        var subgraph = (await Service.GetSubgraph([root.GlobalId], 2)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { root.GlobalId, weapons.GlobalId, ak47.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task EmptyQueryShouldStartFromTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        var child = await Storage.Create(new("child"), firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
        CollectionAssert.DoesNotContain(subgraph.Nodes.Select(static node => node.GlobalId).ToArray(), child.GlobalId);
    }

    [TestMethod]
    public async Task EmptyNodeIdentifierShouldStartFromTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        await Storage.Create(new("child"), firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([new NodePath()], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
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

        var reloadedNewParent = await Service.GetNode(oldParent.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));

        Assert.IsNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
        Assert.IsFalse(child.GetBacking().FolderPath.Contains(oldParent.LocalId));

        Assert.IsNotNull(await Storage.Get(newParent.GlobalId, child.LocalId));
        Assert.IsTrue(child.GetBacking().FolderPath.Contains(newParent.LocalId));
    }

    [TestMethod]
    public async Task EdgesRemove_ShouldThrowWhenHierarchyEdgeIsChildsOnlyEdge() {
        var parent = await Create("parent");
        var child = await Create("child", parent.GlobalId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Service.Disconnect(parent.GlobalId, child.GlobalId));
    }

    private static async Task<InternalId> GetTypedEdgeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService Service)
        where TNodeType : NodeType {
        var result = await Service.GetTypedEdgeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.TypeId;
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
        Assert.AreEqual(nodeTypeId, endpoint.NodeTypeId);
        Assert.AreEqual(isCollection, endpoint.IsCollection);
    }

    
}
