using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

[RelevantTestClass]
public sealed class NodeTypeDslTests
{
    [TestMethod]
    public async Task NodeTypeDefinition_EnforcesRequiredTypedSlot()
    {
        await using var scope = TestGraphStorageScope.Create();
        var weaponTypeState = await CreateNodeTypeAsync(scope, "weapon-type");
        var manufacturerTypeState = await CreateNodeTypeAsync(scope, "manufacturer-type");
        var weaponType = NodeType.FromState(weaponTypeState);
        var manufacturerType = NodeType.FromState(manufacturerTypeState);
        var definition = weaponType.Define(type => type.RequiresSlot("manufacturer", manufacturerType));
        var ak47 = (await scope.Storage.Create(new("ak-47"))).Value!;
        await scope.Storage.Connect(ak47.GlobalId, weaponType.GlobalId);

        var invalid = new InstanceNode((await scope.Storage.Get(ak47.GlobalId)).Value!);

        Assert.ThrowsException<InvalidOperationException>(() => definition.EnsureSatisfiedBy(invalid));

        var manufacturer = (await scope.Storage.Create(new("kalashnikov"))).Value!;
        await scope.Storage.Connect(manufacturer.GlobalId, manufacturerType.GlobalId);
        await scope.Storage.Connect(ak47.GlobalId, manufacturer.GlobalId);
        var valid = new InstanceNode((await scope.Storage.Get(ak47.GlobalId)).Value!);

        definition.EnsureSatisfiedBy(valid);
    }

    [TestMethod]
    public async Task GraphService_AssignNodeTypeAsync_UsesRegisteredNodeTypeDescriptor()
    {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage, typeof(WeaponNodeType).Assembly).InitializeAsync();
        var graph = new GraphData.Core.Services.GraphService(
            scope.Storage,
            new GraphData.Core.Services.GraphSearchService(scope.Storage),
            new CancellationTokensAccessorMock(),
            typeof(WeaponNodeType).Assembly);
        var ak47 = (await scope.Storage.Create(new("ak-47"))).Value!;

        var invalid = await graph.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "Manufacturer");
        var weaponTypeId = await GetNodeTypeIdAsync<WeaponNodeType>(graph);
        var invalidConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsFalse(invalidConnections.Any(node => node.GlobalId == weaponTypeId));

        var country = await graph.CreateNode<CountryNodeType>(new("ussr"));
        Assert.AreEqual(ServiceResultStatus.Ok, country.Status, country.Error);
        var manufacturer = (await scope.Storage.Create(new("kalashnikov"), attributes: new Dictionary<string, string> {
            [nameof(ManufacturerNodeType.LegalName)] = "Kalashnikov Concern",
            [nameof(ManufacturerNodeType.FoundedYear)] = "1807",
            [nameof(ManufacturerNodeType.IsActive)] = "true"
        })).Value!;
        await graph.ConnectNodesAsync(manufacturer.GlobalId, country.Value!.GlobalId);
        var assignManufacturer = await graph.AssignNodeTypeAsync<ManufacturerNodeType>(manufacturer.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, assignManufacturer.Status, assignManufacturer.Error);
        await graph.ConnectNodesAsync(ak47.GlobalId, manufacturer.GlobalId);

        var valid = await graph.AssignNodeTypeAsync<WeaponNodeType>(ak47.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        var validConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsTrue(validConnections.Any(node => node.GlobalId == weaponTypeId));
    }

    [TestMethod]
    public async Task GraphService_AssignNodeTypeAsync_UsesGraphTypeTopologyInsteadOfInternalIdShape()
    {
        await using var scope = TestGraphStorageScope.Create();
        var graph = new GraphData.Core.Services.GraphService(
            scope.Storage,
            new GraphData.Core.Services.GraphSearchService(scope.Storage),
            new CancellationTokensAccessorMock());
        var arbitraryType = await CreateNodeTypeAsync(scope, "weapon-type");
        await CreatePathAsync(scope.Storage, GraphBaseTypeIds.NodeType);
        var pathShapedNonType = (await scope.Storage.Create(new("Fake"), GraphSystemNodeIds.NodeTypeRoot)).Value!;
        var ak47 = (await scope.Storage.Create(new("ak-47"))).Value!;
        var m16 = (await scope.Storage.Create(new("m16"))).Value!;
        var fnFal = (await scope.Storage.Create(new("fn-fal"))).Value!;

        var arbitraryResult = await graph.AssignNodeTypeAsync(ak47.GlobalId, arbitraryType.GlobalId);
        var pathShapedResult = await graph.AssignNodeTypeAsync(m16.GlobalId, pathShapedNonType.GlobalId);
        var rootResult = await graph.AssignNodeTypeAsync(fnFal.GlobalId, GraphSystemNodeIds.NodeTypeRoot);

        Assert.AreEqual(ServiceResultStatus.Ok, arbitraryResult.Status, arbitraryResult.Error);
        Assert.AreEqual(ServiceResultStatus.BadRequest, pathShapedResult.Status);
        StringAssert.Contains(pathShapedResult.Error, "not a node type");
        Assert.AreEqual(ServiceResultStatus.BadRequest, rootResult.Status);
        StringAssert.Contains(rootResult.Error, "not a node type");
    }

    [TestMethod]
    public async Task NodeTypeDefinition_DiscoversRichCSharpFields()
    {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage, typeof(ManufacturerNodeType).Assembly).InitializeAsync();
        var graph = new GraphData.Core.Services.GraphService(
            scope.Storage,
            new GraphData.Core.Services.GraphSearchService(scope.Storage),
            new CancellationTokensAccessorMock(),
            typeof(ManufacturerNodeType).Assembly);

        var result = await graph.GetNodeTypeDefinitionAsync<ManufacturerNodeType>();

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var definition = result.Value!;
        var countryTypeId = await GetNodeTypeIdAsync<CountryNodeType>(graph);
        var manufacturerTypeId = await GetNodeTypeIdAsync<ManufacturerNodeType>(graph);
        var weaponTypeId = await GetNodeTypeIdAsync<WeaponNodeType>(graph);

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

    private static void AssertField(
        NodeTypeDefinition definition,
        string name,
        NodeFieldValueKind kind,
        Type clrType,
        NodeSlotCardinality cardinality,
        InternalId? nodeTypeId = null,
        bool isCollection = false)
    {
        var field = definition.Fields.Single(value => value.Name == name);
        Assert.AreEqual(kind, field.ValueKind);
        Assert.AreEqual(clrType, field.ClrType);
        Assert.AreEqual(cardinality, field.Cardinality);
        Assert.AreEqual(nodeTypeId, field.NodeTypeId);
        Assert.AreEqual(isCollection, field.IsCollection);
    }

    private static async Task<NodeState> CreateNodeTypeAsync(TestGraphStorageScope scope, string localId)
    {
        await CreatePathAsync(scope.Storage, GraphBaseTypeIds.NodeType);
        var type = (await scope.Storage.Create(new(localId))).Value!;
        var connect = await scope.Storage.Connect(type.GlobalId, GraphBaseTypeIds.NodeType);
        Assert.AreEqual(ServiceResultStatus.Ok, connect.Status, connect.Error);
        return type;
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

    private static async Task<InternalId> GetNodeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService graph)
        where TNodeType : NodeType
    {
        var result = await graph.GetNodeTypeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.Type.GlobalId;
    }

    private sealed class WeaponNodeType : NodeType
    {
        public ManufacturerNodeType Manufacturer = null!;
    }

    private sealed class CountryNodeType : NodeType
    {
    }

    private sealed class ManufacturerNodeType : NodeType
    {
        public CountryNodeType Country = null!;
        public ManufacturerNodeType? ParentCompany = null;
        public IReadOnlyCollection<WeaponNodeType> ProducedWeapons = [];
        public Node Headquarters = null!;
        public Node? ArchiveNode = null;
        public string LegalName = "";
        public int FoundedYear = 0;
        public bool IsActive = false;
        public decimal? AnnualRevenueUsd = null;
        public string? Website = null;
        public IReadOnlyCollection<string> Aliases = [];
    }
}
