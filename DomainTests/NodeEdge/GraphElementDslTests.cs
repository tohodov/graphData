using System;
using System.Collections.Generic;
using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Models;

[RelevantTestClass]
public sealed class GraphElementDslTests
{
    [TestMethod]
    public void Node_ShouldDelegateToStateForDslTypes()
    {
        var state = new VirtualNodeState("node");
        var type = (NodeType)null! ?? throw new Exception();
        var node = new InstanceNode(state, type);

        node.Attributes["name"] = "demo";

        Assert.AreEqual(new NodeLocalId("node"), node.LocalId);
        Assert.AreEqual(new InternalId("node"), node.GlobalId);
        Assert.AreEqual("demo", state.Attributes["name"]);
    }
}
