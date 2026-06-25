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
using static GraphData.Tests.GraphService.NodeTypeDslTests;

public sealed class TypedEdgeNodeTypeDslTests : GraphServiceTests {
    static Assembly[] todoDelete = [typeof(ManufacturedByConnectionNodeType).Assembly];
    protected override Assembly[] Assemblies => todoDelete;

    [TestMethod]
    public async Task GraphService_ChangeEdgeTypeAsync_CreatesMetadataFreeTypedEdgeSubgraph() {
        var weapon = await Service.CreateNode<EdgeWeaponNodeType>(new("ak-47"));
        Assert.AreEqual(ServiceResultStatus.Ok, weapon.Status, weapon.Error);
        var manufacturer = await Service.CreateNode<EdgeManufacturerNodeType>(new("kalashnikov"));
        Assert.AreEqual(ServiceResultStatus.Ok, manufacturer.Status, manufacturer.Error);
        await Service.ConnectNodesAsync(weapon.Value!.GlobalId, manufacturer.Value!.GlobalId);

        var result = await Service.ChangeEdgeTypeAsync<ManufacturedByConnectionNodeType>(
            weapon.Value.GlobalId,
            manufacturer.Value.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var relationId = new InternalId("manufactured-by-1");
        var relation = await Storage.Get(relationId);
        Assert.IsNotNull(relation);
        AssertMetadataFree(relation);

        var weaponEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByConnectionNodeType.Weapon)));
        var manufacturerEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByConnectionNodeType.Manufacturer)));
        var weaponEndpoint = await Storage.Get(weaponEndpointId);
        Assert.IsNotNull(weaponEndpoint);
        var manufacturerEndpoint = await Storage.Get(manufacturerEndpointId);
        Assert.IsNotNull(manufacturerEndpoint);
        AssertMetadataFree(weaponEndpoint);
        AssertMetadataFree(manufacturerEndpoint);

        var edgeTypeId = await GetTypedEdgeTypeIdAsync<ManufacturedByConnectionNodeType>(Service);
        var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(Service);
        var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(Service);
        var weaponEndpointSpec = await GetEndpointSpecAsync(weaponEndpointId, relationId, weapon.Value.GlobalId);
        var manufacturerEndpointSpec = await GetEndpointSpecAsync(manufacturerEndpointId, relationId, manufacturer.Value.GlobalId);

        Assert.IsTrue(relation.Nodes.Any(node => node.GlobalId == edgeTypeId));
        Assert.IsTrue((await Storage.Get(edgeTypeId))!.Nodes.Any(node => node.GlobalId == Service.TypesRoot.GlobalId));
        await AssertConnected(weaponEndpointId, Service.TypesRoot.GlobalId);
        await AssertConnected(manufacturerEndpointId, Service.TypesRoot.GlobalId);
        await AssertConnected(weaponEndpointId, weapon.Value.GlobalId);
        await AssertConnected(weaponEndpointId, weaponEndpointSpec.GlobalId);
        await AssertConnected(manufacturerEndpointId, manufacturer.Value.GlobalId);
        await AssertConnected(manufacturerEndpointId, manufacturerEndpointSpec.GlobalId);
        await AssertConnected(weaponEndpointSpec.GlobalId, weaponNodeTypeId);
        await AssertConnected(manufacturerEndpointSpec.GlobalId, manufacturerNodeTypeId);

        var weaponConnections = await Storage.GetNeighbors(weapon.Value.GlobalId).ToArrayAsync();
        Assert.IsFalse(weaponConnections.Any(node => node.GlobalId == manufacturer.Value.GlobalId));

        var returnedIds = result.Value!.Nodes.Select(static node => node.GlobalId).ToArray();
        CollectionAssert.IsSubsetOf(
            new[] {
                relationId,
                edgeTypeId,
                weapon.Value.GlobalId,
                manufacturer.Value.GlobalId,
                weaponEndpointId,
                manufacturerEndpointId,
                weaponEndpointSpec.GlobalId,
                manufacturerEndpointSpec.GlobalId
            },
            returnedIds);
        async Task AssertConnected(NodeRef first, InternalId second) => Assert.IsTrue((await Storage.Get(first))!.Nodes.Any(node => node.GlobalId == second));
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
        Assert.IsFalse(ak47.Nodes.Any(node => node.GlobalId == weaponTypeId));

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
        Assert.IsTrue(ak47.Nodes.Any(node => node.GlobalId == weaponTypeId));
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
    }

    [TestMethod]
    public async Task NodeTypeDefinition_EnforcesRequiredTypedSlot() {
        var weaponTypeState = await Storage.Create("weapon-type", Service.TypesRoot.GlobalId);
        var manufacturerTypeState = await Storage.Create("manufacturer-type", Service.TypesRoot.GlobalId);
        var weaponType = await Service.GetTypeNode(weaponTypeState.GlobalId)!;
        var manufacturerType = await Service.GetTypeNode(manufacturerTypeState.GlobalId)!;
        var builder = new NodeTypeBuilder(weaponType!, type => new InternalId());
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
        var arbitraryType = await Service.CreateNode("weapon-type");
        var pathShapedNonType = await Storage.Create(new("Fake"), GraphSystemNodeIds.NodeTypeRoot);
        var ak47 = await Storage.Create(new("ak-47"));
        var m16 = await Storage.Create(new("m16"));
        var fnFal = await Storage.Create(new("fn-fal"));

        var arbitraryResult = await Service.AssignNodeTypeAsync(ak47.GlobalId, arbitraryType.Value!.GlobalId);
        var pathShapedResult = await Service.AssignNodeTypeAsync(m16.GlobalId, pathShapedNonType.GlobalId);
        var rootResult = await Service.AssignNodeTypeAsync(fnFal.GlobalId, GraphSystemNodeIds.NodeTypeRoot);

        Assert.AreEqual(ServiceResultStatus.Ok, arbitraryResult.Status, arbitraryResult.Error);
        Assert.AreEqual(ServiceResultStatus.BadRequest, pathShapedResult.Status);
        StringAssert.Contains(pathShapedResult.Error, "not a node type");
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

        var subgraph = (await Service.GetSubgraph([new InternalId()], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
    }
    [TestMethod]
    public async Task AddSubgraph_ShouldPersistVirtualNodesAndConnections() {
        var catalog = new Node(new InternalId("catalog"));
        var weapon = new Node(new InternalId("catalog", "ak-47"));
        weapon.Attributes["displayName"] = "AK-47";
        var weaponType = new Node(new InternalId("graphdata", "types", "nodes", "Weapon"));

        catalog.Nodes.Add(weapon);
        weapon.Nodes.Add(weaponType);

        var result = await Service.AddSubgraph(catalog);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        CollectionAssert.IsSubsetOf(
            new[] { catalog.GlobalId, weapon.GlobalId, weaponType.GlobalId },
            result.Value!.Nodes.Select(static node => node.GlobalId).ToArray());

        var persistedWeapon = await Storage.Get(weapon.GlobalId);
        Assert.IsNotNull(persistedWeapon);
        Assert.AreEqual("AK-47", persistedWeapon.Attributes["displayName"]);
        Assert.IsNotNull(await Storage.Get(GraphSystemNodeIds.NodeTypeRoot));

        Assert.IsTrue(persistedWeapon.Nodes.Any(node => node.GlobalId == weaponType.GlobalId));
    }

    [TestMethod]
    public async Task AddSubgraph_ShouldPreserveExistingNodeAttributes() {
        await Storage.Create(
            new("catalog"),
            attributes: new Dictionary<string, string> {
                ["color"] = "#123456"
            });
        var catalog = new Node(new InternalId("catalog"));
        catalog.Attributes["color"] = "#abcdef";
        catalog.Attributes["generated"] = "true";

        var result = await Service.AddSubgraph(catalog);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var persisted = await Storage.Get(catalog.GlobalId);
        Assert.IsNotNull(persisted);
        Assert.AreEqual("#123456", persisted.Attributes["color"]);
        Assert.IsFalse(persisted.Attributes.ContainsKey("generated"));
    }

    private static string EndpointInstanceLocalId(string endpointName) =>
        $"endpoint-{endpointName}";

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

    private static void AssertMetadataFree(NodeState node) {
        Assert.AreEqual(0, node.Attributes.Count);
        Assert.IsFalse(node.Attributes.ContainsKey("Service.kind"));
        Assert.IsFalse(node.Attributes.ContainsKey("Service.role"));
        Assert.IsFalse(node.Attributes.ContainsKey("Service.typeName"));
    }

    private async Task<NodeState> GetEndpointSpecAsync(
        InternalId endpointInstanceId,
        InternalId relationId,
        InternalId endpointNodeId) {
        var endpoint = await Storage.Get(endpointInstanceId);
        Assert.IsNotNull(endpoint);
        var spec = endpoint.Nodes.Single(node =>
            node.GlobalId != relationId
            && node.GlobalId != endpointNodeId
            /*&& node.GlobalId != GraphBaseTypeIds.Endpoint*/);
        return spec;
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

    private sealed class EdgeWeaponNodeType : NodeType {
        internal EdgeWeaponNodeType(NodeState state) : base(state) {
        }
    }

    private sealed class EdgeManufacturerNodeType : NodeType {
        internal EdgeManufacturerNodeType(NodeState state) : base(state) {
        }
    }

    private sealed class ManufacturedByConnectionNodeType : NodeType {
        public EdgeWeaponNodeType Weapon = null!;
        public EdgeManufacturerNodeType Manufacturer = null!;

        internal ManufacturedByConnectionNodeType(NodeState state) : base(state) {
        }
    }

    private sealed class ShipmentConnectionNodeType : NodeType {
        public EdgeWeaponNodeType Weapon = null!;
        public Node Counterparty = null!;
        public Node? OptionalWaypoint = null;
        public IReadOnlyCollection<EdgeManufacturerNodeType> Manufacturers = [];
        public string Note = "";

        internal ShipmentConnectionNodeType(NodeState state) : base(state) {
        }
    }
}
