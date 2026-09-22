using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class CarrierNamingCompatibilityTests
{
    [TestMethod]
    public void PersistedClrNamesRemainReadableAndAreNotMigratedByRename() {
        Assert.AreEqual(typeof(CarrierNode), CarrierClrTypeNames.Resolve("Node, Domain"));
        Assert.AreEqual(typeof(CarrierEdge), CarrierClrTypeNames.Resolve("Edge, Domain"));
        Assert.AreEqual(typeof(CarrierNode[]), CarrierClrTypeNames.Resolve("Node[], Domain"));
        StringAssert.StartsWith(CarrierClrTypeNames.Serialize(typeof(CarrierNode)), "Node, Domain,");
        StringAssert.StartsWith(CarrierClrTypeNames.Serialize(typeof(CarrierEdge)), "Edge, Domain,");
        Assert.AreEqual(typeof(string), CarrierClrTypeNames.Resolve(CarrierClrTypeNames.Serialize(typeof(string))));
        Assert.IsNull(CarrierClrTypeNames.Resolve("UnknownCarrierType, Domain"));
    }

    [TestMethod]
    public void InfrastructureTypeIdsKeepHistoricalSuffixes() {
        Assert.AreEqual("InstanceOf", NodeType.CreateDefaultLocalId(typeof(InstanceOfEdge)).ToString());
        Assert.AreEqual("Requires", NodeType.CreateDefaultLocalId(typeof(RequiresEdge)).ToString());
    }
}
