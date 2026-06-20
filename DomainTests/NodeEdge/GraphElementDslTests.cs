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
        var state = new InMemoryNodeState("node");
        var node = new InstanceNode(state);

        node.Attributes["name"] = "demo";

        Assert.AreEqual(new NodeLocalId("node"), node.LocalId);
        Assert.AreEqual(new InternalId("node"), node.GlobalId);
        Assert.AreEqual("demo", state.Attributes["name"]);
        Assert.AreEqual(GraphBaseTypeIds.NodeInstance, node.TypeId);
    }

    [TestMethod]
    public void TypeAndInstance_ShouldExposeBaseTypeIds()
    {
        var typeState = new InMemoryNodeState("type-node");
        var instanceState = new InMemoryNodeState("instance-node");
        var typeNode = NodeType.FromState(typeState);
        var instanceNode = new InstanceNode(instanceState);

        Assert.AreEqual(GraphBaseTypeIds.NodeInstance, InstanceNode.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.NodeType, new InternalId("graphdata", "types", "nodes", "Type"));
        Assert.AreEqual(GraphBaseTypeIds.Relation, new InternalId("graphdata", "types", "nodes", "Relation"));
        Assert.AreEqual(GraphBaseTypeIds.Endpoint, new InternalId("graphdata", "types", "nodes", "Endpoint"));
        Assert.AreEqual(GraphBaseTypeIds.Port, new InternalId("graphdata", "types", "nodes", "Port"));
        Assert.AreEqual(typeNode.GlobalId, typeState.GlobalId);
        Assert.AreEqual(instanceNode.GlobalId, instanceState.GlobalId);
    }

    [TestMethod]
    public void SystemNodeIds_ShouldExposeRuntimeTypeRoots()
    {
        Assert.AreEqual(new InternalId("graphdata", "types", "nodes"), GraphSystemNodeIds.NodeTypeRoot);
        Assert.AreEqual(GraphSystemNodeIds.NodeTypeRoot, GraphBaseTypeIds.NodeTypeRoot);
    }

    private sealed class InMemoryNodeState(string name) : NodeState
    {
        public override NodeLocalId LocalId { get; } = new(name);
        public override InternalId GlobalId { get; } = new(name);
        public override ICollection<EdgeState> Edges { get; } = Array.Empty<EdgeState>();
        public override ILazyCollection<NodeState> Nodes => throw new NotSupportedException();
        public override IDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

}
