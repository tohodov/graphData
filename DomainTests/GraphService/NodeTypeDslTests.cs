using System.Linq;
using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

[RelevantTestClass]
public sealed class NodeTypeDslTests
{
    [TestMethod]
    public void NodeTypeValidator_EnforcesRequiredTypedSlot()
    {
        var weaponType = new NodeGlobalId("graphdata", "types", "nodes", "Weapon");
        var manufacturerType = new NodeGlobalId("graphdata", "types", "nodes", "Manufacturer");
        var schema = NodeTypeSchema.Create([
            NodeTypeDefinition.Define(weaponType, type => type.RequiresSlot("manufacturer", manufacturerType)),
            NodeTypeDefinition.Define(manufacturerType)
        ]);
        var invalid = new TypedNodeInstance(
            new NodeGlobalId("ak-47"),
            [new NodeTypeId(weaponType)],
            []);
        var valid = new TypedNodeInstance(
            new NodeGlobalId("ak-47"),
            [new NodeTypeId(weaponType)],
            [
                new TypedNodeNeighbor(
                    new NodeGlobalId("kalashnikov"),
                    [new NodeTypeId(manufacturerType)])
            ]);

        var invalidResult = NodeTypeValidator.Validate(schema, invalid);
        var validResult = NodeTypeValidator.Validate(schema, valid);

        Assert.IsFalse(invalidResult.IsValid);
        Assert.IsTrue(invalidResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "node-type.slot-cardinality"));
        Assert.IsTrue(validResult.IsValid);
    }
}
