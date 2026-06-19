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
        var graphData = (await scope.Storage.Create(new("graphdata"))).Value!;
        var typeRoot = (await scope.Storage.Create(new("types"), graphData.GlobalId)).Value!;
        var nodeTypeRoot = (await scope.Storage.Create(new("nodes"), typeRoot.GlobalId)).Value!;
        var weaponTypeState = (await scope.Storage.Create(new("Weapon"), nodeTypeRoot.GlobalId)).Value!;
        var manufacturerTypeState = (await scope.Storage.Create(new("Manufacturer"), nodeTypeRoot.GlobalId)).Value!;
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

        var invalid = await graph.AssignNodeTypeAsync<WeaponNodeType>(Segments(ak47.GlobalId));

        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "Manufacturer");
        var invalidConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsFalse(invalidConnections.Any(node => node.GlobalId == TypeId<WeaponNodeType>()));

        var country = await graph.CreateNode<CountryNodeType>(new("ussr"));
        Assert.AreEqual(ServiceResultStatus.Ok, country.Status, country.Error);
        var manufacturer = (await scope.Storage.Create(new("kalashnikov"), attributes: new Dictionary<string, string> {
            [nameof(ManufacturerNodeType.LegalName)] = "Kalashnikov Concern",
            [nameof(ManufacturerNodeType.FoundedYear)] = "1807",
            [nameof(ManufacturerNodeType.IsActive)] = "true"
        })).Value!;
        await graph.ConnectNodesAsync(Segments(manufacturer.GlobalId), Segments(country.Value!.GlobalId));
        var assignManufacturer = await graph.AssignNodeTypeAsync<ManufacturerNodeType>(Segments(manufacturer.GlobalId));
        Assert.AreEqual(ServiceResultStatus.Ok, assignManufacturer.Status, assignManufacturer.Error);
        await graph.ConnectNodesAsync(Segments(ak47.GlobalId), Segments(manufacturer.GlobalId));

        var valid = await graph.AssignNodeTypeAsync<WeaponNodeType>(Segments(ak47.GlobalId));

        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        var validConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsTrue(validConnections.Any(node => node.GlobalId == TypeId<WeaponNodeType>()));
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
        Assert.AreEqual(TypeId<ManufacturerNodeType>(), definition.Type.GlobalId);
        AssertField(definition, nameof(ManufacturerNodeType.Country), NodeFieldValueKind.Node, typeof(CountryNodeType), NodeSlotCardinality.Required(), TypeId<CountryNodeType>());
        AssertField(definition, nameof(ManufacturerNodeType.ParentCompany), NodeFieldValueKind.Node, typeof(ManufacturerNodeType), NodeSlotCardinality.Optional(), TypeId<ManufacturerNodeType>());
        AssertField(definition, nameof(ManufacturerNodeType.ProducedWeapons), NodeFieldValueKind.Node, typeof(WeaponNodeType), NodeSlotCardinality.Many(), TypeId<WeaponNodeType>(), isCollection: true);
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

    private static string[] Segments(NodeGlobalId id) =>
        id.Select(static segment => segment.ToString()).ToArray();

    private static NodeGlobalId TypeId<TNodeType>()
    {
        var name = typeof(TNodeType).Name;
        if (name.EndsWith(nameof(NodeType), StringComparison.Ordinal))
            name = name[..^nameof(NodeType).Length];
        return new NodeGlobalId("graphdata", "types", "nodes", name);
    }

    private static void AssertField(
        NodeTypeDefinition definition,
        string name,
        NodeFieldValueKind kind,
        Type clrType,
        NodeSlotCardinality cardinality,
        NodeGlobalId? nodeTypeId = null,
        bool isCollection = false)
    {
        var field = definition.Fields.Single(value => value.Name == name);
        Assert.AreEqual(kind, field.ValueKind);
        Assert.AreEqual(clrType, field.ClrType);
        Assert.AreEqual(cardinality, field.Cardinality);
        Assert.AreEqual(nodeTypeId, field.NodeTypeId);
        Assert.AreEqual(isCollection, field.IsCollection);
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
