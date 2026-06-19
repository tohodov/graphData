using System;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
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
}
