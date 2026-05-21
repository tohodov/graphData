using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[TestClass]
public sealed class GraphControllerTests
{
    [TestMethod]
    public async Task GetNodeAsync_ReturnsNodeWithNeighborEdges()
    {
        var first = new TestNode("1", new Dictionary<string, string> { ["kind"] = "root" });
        var second = new TestNode("2", new Dictionary<string, string> { ["kind"] = "leaf" });
        var storage = new FakeGraphStorage([first, second]);
        storage.Connect(first.Name, second);

        var controller = CreateController(storage);
        var result = await controller.GetNodeAsync(first.Name);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(first.Name, response.Name);
        Assert.AreEqual("root", response.Attributes["kind"]);

        var edge = response.Edges.Single();
        Assert.AreEqual(first.Name, edge.SourceName);
        Assert.AreEqual(second.Name, edge.TargetName);
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNotFoundForMissingNode()
    {
        var controller = CreateController(new FakeGraphStorage([]));

        var result = await controller.GetNodeAsync("missing");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsTopLevelEdgesWithoutDuplicatingThemOnNodes()
    {
        var first = new TestNode("1");
        var second = new TestNode("2");
        var third = new TestNode("3");
        first.SetConnections([second]);
        second.SetConnections([first, third]);
        third.SetConnections([second]);
        var storage = new FakeGraphStorage([first, second, third]);
        storage.Connect(first.Name, second);
        storage.Connect(second.Name, third);

        var controller = CreateController(storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest
        {
            RootNodeIds = [first.Name],
            MaxDepth = 1
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(new[] { "1", "2" }, response.Nodes.Select(static node => node.Name).ToArray());
        Assert.IsTrue(response.Nodes.All(static node => node.Edges.Count == 0));

        var edge = response.Edges.Single();
        Assert.AreEqual(first.Name, edge.SourceName);
        Assert.AreEqual(second.Name, edge.TargetName);
    }

    private static GraphController CreateController(FakeGraphStorage storage)
    {
        return new GraphController(new NodeService(storage), new GraphSearchService(storage));
    }

    private sealed class FakeGraphStorage(IEnumerable<TestNode> nodes) : IGraphStorage
    {
        private readonly Dictionary<string, TestNode> _nodes = nodes.ToDictionary(
            static node => node.Name,
            StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<Node>> _connections = new(StringComparer.OrdinalIgnoreCase);

        public Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null)
        {
            throw new NotSupportedException();
        }

        public Task<Node?> Get(Node? parent, string subNodeName)
        {
            return Task.FromResult<Node?>(_nodes.GetValueOrDefault(subNodeName));
        }

        public Task<Node?> Get(NodeQuery query)
        {
            throw new NotSupportedException();
        }

        public Task Update(string subNodeName, IDictionary<string, string> attributes, Node? parent = null)
        {
            throw new NotSupportedException();
        }

        public Task Connect(Node sourceNode, Node targetNode)
        {
            Connect(sourceNode.Name, targetNode);
            Connect(targetNode.Name, sourceNode);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node)
        {
            return Task.FromResult<IReadOnlyCollection<Node>>(
                _connections.TryGetValue(node.Name, out var connections)
                    ? connections
                    : Array.Empty<Node>());
        }

        public Task<Subgraph> GetSubgraphAsync(SubgraphQuery query)
        {
            var allowed = query.RootNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (query.MaxDepth > 0)
            {
                foreach (var root in query.RootNodeIds)
                {
                    if (_connections.TryGetValue(root, out var connections))
                    {
                        foreach (var connection in connections)
                        {
                            allowed.Add(connection.Name);
                        }
                    }
                }
            }

            var nodes = allowed
                .Select(name => _nodes.GetValueOrDefault(name))
                .Where(static node => node is not null)
                .Cast<Node>()
                .ToArray();
            return Task.FromResult(new Subgraph { Nodes = nodes });
        }

        internal void Connect(string sourceName, Node target)
        {
            if (!_connections.TryGetValue(sourceName, out var connections))
            {
                connections = [];
                _connections[sourceName] = connections;
            }

            connections.Add(target);
        }
    }

    private sealed record TestNode(
        string NodeName,
        IReadOnlyDictionary<string, string>? AttributeSnapshot = null) : Node
    {
        private IReadOnlyCollection<Node> _nodes = Array.Empty<Node>();

        public override string Name => NodeName;

        public override IReadOnlyDictionary<string, Edge> Edges { get; } =
            new Dictionary<string, Edge>(StringComparer.OrdinalIgnoreCase);

        public override IReadOnlyCollection<Node> Nodes => _nodes;

        public override IReadOnlyDictionary<string, string> Attributes =>
            AttributeSnapshot ?? new Dictionary<string, string>();

        internal void SetConnections(IReadOnlyCollection<Node> nodes)
        {
            _nodes = nodes;
        }
    }
}
