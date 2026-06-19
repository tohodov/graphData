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
        Assert.AreEqual(new NodeGlobalId("node"), node.GlobalId);
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
        var edgeState = new InMemoryEdgeState(typeState, instanceState);
        var typeEdge = new TypeEdge(edgeState);
        var instanceEdge = new InstanceEdge(edgeState);

        Assert.AreEqual(GraphBaseTypeIds.NodeInstance, InstanceNode.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeType, TypeEdge.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeInstance, InstanceEdge.StaticTypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeType, typeEdge.TypeId);
        Assert.AreEqual(GraphBaseTypeIds.EdgeInstance, instanceEdge.TypeId);
        Assert.AreEqual(typeNode.GlobalId, typeEdge.Node1.GlobalId);
        Assert.AreEqual(instanceNode.GlobalId, typeEdge.Node2.GlobalId);
    }

    [TestMethod]
    public void SystemNodeIds_ShouldExposeRuntimeTypeRoots()
    {
        Assert.AreEqual(new NodeGlobalId("graphdata", "types", "nodes"), GraphSystemNodeIds.NodeTypeRoot);
        Assert.AreEqual(new NodeGlobalId("graphdata", "types", "edges"), GraphSystemNodeIds.EdgeTypeRoot);
        Assert.AreEqual(new NodeGlobalId("graphdata", "relations"), GraphSystemNodeIds.RelationRoot);
        Assert.AreEqual(GraphSystemNodeIds.NodeTypeRoot, GraphBaseTypeIds.NodeTypeRoot);
        Assert.AreEqual(GraphSystemNodeIds.EdgeTypeRoot, GraphBaseTypeIds.EdgeTypeRoot);
    }

    private sealed class InMemoryNodeState(string name) : NodeState
    {
        public override NodeLocalId LocalId { get; } = new(name);
        public override NodeGlobalId GlobalId { get; } = new(name);
        public override ICollection<EdgeState> Edges { get; } = Array.Empty<EdgeState>();
        public override ILazyCollection<NodeState> Nodes => throw new NotSupportedException();
        public override IDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InMemoryEdgeState(NodeState node1, NodeState node2) : EdgeState
    {
        public override NodeState Node1 { get; } = node1;
        public override NodeState Node2 { get; } = node2;
    }
}
