using System;
using System.Collections.Generic;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Models;

[TestClass]
public sealed class GraphElementDslTests
{
    [TestMethod]
    public void Node_ShouldDelegateToStateForDslTypes()
    {
        var state = new InMemoryNodeState("node");
        Node node = new InstanceNode(state);

        node.Attributes["name"] = "demo";

        Assert.AreEqual(new NodeLocalId("node"), node.LocalId);
        Assert.AreEqual(new NodeGlobalId("node"), node.GlobalId);
        Assert.AreEqual("demo", state.Attributes["name"]);
        Assert.AreEqual(GraphBaseTypeIds.NodeInstance, node.TypeId);
    }

    [TestMethod]
    public void TypeAndInstance_ShouldExposeBaseTypeIds()
    {
        var typeNode = new TypeNode(new InMemoryNodeState("type-node"));
        var instanceNode = new InstanceNode(new InMemoryNodeState("instance-node"));
        var edgeState = new InMemoryEdgeState(typeNode, instanceNode);
        var typeEdge = new TypeEdge(edgeState);
        var instanceEdge = new InstanceEdge(edgeState);

        Assert.AreEqual(GraphBaseTypeIds.NodeType, TypeNode.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.NodeInstance, InstanceNode.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeType, TypeEdge.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeInstance, InstanceEdge.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.NodeType, typeNode.TypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeType, typeEdge.TypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeInstance, instanceEdge.TypeId);
        Assert.AreSame(typeNode, typeEdge.Node1);
        Assert.AreSame(instanceNode, typeEdge.Node2);
    }

    private sealed class InMemoryNodeState(string name) : NodeState
    {
        public override NodeLocalId LocalId { get; } = new(name);
        public override NodeGlobalId GlobalId { get; } = new(name);
        public override ICollection<Edge> Edges { get; } = Array.Empty<Edge>();
        public override ICollection<Node> Nodes { get; } = Array.Empty<Node>();
        public override IDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InMemoryEdgeState(Node node1, Node node2) : EdgeState
    {
        public override Node Node1 { get; } = node1;
        public override Node Node2 { get; } = node2;
    }
}
