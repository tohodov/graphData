using System;
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
        var weaponType = new TypeNode(weaponTypeState);
        var manufacturerType = new TypeNode(manufacturerTypeState);
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

        var invalid = await graph.AssignNodeTypeAsync(
            Segments(ak47.GlobalId),
            Segments(WeaponNodeType.StaticTypeId));

        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "manufacturer");
        var invalidConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsFalse(invalidConnections.Any(node => node.GlobalId == WeaponNodeType.StaticTypeId));

        var manufacturer = await graph.CreateNode<ManufacturerNodeType>(new("kalashnikov"));
        Assert.AreEqual(ServiceResultStatus.Ok, manufacturer.Status, manufacturer.Error);
        await graph.ConnectNodesAsync(Segments(ak47.GlobalId), Segments(manufacturer.Value!.GlobalId));

        var valid = await graph.AssignNodeTypeAsync(
            Segments(ak47.GlobalId),
            Segments(WeaponNodeType.StaticTypeId));

        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        var validConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(ak47.GlobalId)).Value!)).Value!;
        Assert.IsTrue(validConnections.Any(node => node.GlobalId == WeaponNodeType.StaticTypeId));
    }

    private static string[] Segments(NodeGlobalId id) =>
        id.Select(static segment => segment.ToString()).ToArray();

    private sealed class WeaponNodeType : NodeType
    {
        public static NodeGlobalId StaticTypeId { get; } = new("graphdata", "types", "nodes", "Weapon");

        public override void Define(NodeTypeBuilder type)
        {
            type.RequiresSlot<ManufacturerNodeType>("manufacturer");
        }
    }

    private sealed class ManufacturerNodeType : NodeType
    {
        public static NodeGlobalId StaticTypeId { get; } = new("graphdata", "types", "nodes", "Manufacturer");
    }
}
