using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    public async Task CreateNodeAsync_ReturnsCreatedNodeWithLocation()
    {
        var storage = new FakeGraphStorage([]);
        var controller = CreateController(storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest
        {
            Name = "new node",
            Attributes = new Dictionary<string, string> { ["kind"] = "demo" }
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        StringAssert.Contains(created.Location, "/api/graph/nodes?name=new%20node");

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("new node", response.Name);
        Assert.AreEqual("demo", response.Attributes["kind"]);
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

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsVariableBindings()
    {
        var first = new TestNode("1", new Dictionary<string, string> { ["id"] = "source" });
        var second = new TestNode("2", new Dictionary<string, string> { ["id"] = "Y" });
        var storage = new FakeGraphStorage([first, second]);
        storage.Connect(first.Name, second);
        storage.Connect(second.Name, first);

        var controller = CreateController(storage);
        var result = await controller.SearchNodesAsync(new NodeSearchQuery
        {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpression
            {
                Expressions = [
                    new NodeConnectedSearchExpression {
                        Left = new NodeVariableSearchSelector { Name = "n" },
                        Right = new NodeVariableSearchSelector { Name = "x" }
                    },
                    new NodeAttributeSearchExpression {
                        Node = new NodeVariableSearchSelector { Name = "x" },
                        Key = "id",
                        Value = "Y"
                    }
                ]
            }
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeSearchResponse;
        Assert.IsNotNull(response);
        var match = response.Matches.Single();
        Assert.AreEqual(first.Name, match.Bindings["n"].Name);
        Assert.AreEqual(second.Name, match.Bindings["x"].Name);
    }

    [TestMethod]
    public void NodeSearchQueryJson_ShouldDeserializePredicateTree()
    {
        const string json = """
            {
              "return": [ "n", "x" ],
              "where": {
                "kind": "all",
                "expressions": [
                  {
                    "kind": "connected",
                    "left": { "kind": "var", "name": "n" },
                    "right": { "kind": "var", "name": "x" }
                  },
                  {
                    "kind": "attribute",
                    "node": { "kind": "var", "name": "x" },
                    "key": "id",
                    "operator": "equals",
                    "value": "Y"
                  }
                ]
              },
              "limit": 20
            }
            """;

        var query = JsonSerializer.Deserialize<NodeSearchQuery>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(query);
        CollectionAssert.AreEquivalent(new[] { "n", "x" }, query.Return);
        Assert.IsInstanceOfType(query.Where, typeof(AllNodeSearchExpression));
        var all = (AllNodeSearchExpression)query.Where!;
        Assert.IsInstanceOfType(all.Expressions[0], typeof(NodeConnectedSearchExpression));
        Assert.IsInstanceOfType(all.Expressions[1], typeof(NodeAttributeSearchExpression));
    }

    private static GraphController CreateController(FakeGraphStorage storage)
    {
        return new GraphController(new NodeService(storage), new GraphSearchService(storage));
    }

    private sealed class FakeGraphStorage(IEnumerable<TestNode> nodes) : IGraphStorage, IGraphNodeCatalog
    {
        private readonly Dictionary<string, TestNode> _nodes = nodes.ToDictionary(
            static node => node.Name,
            StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<Node>> _connections = new(StringComparer.OrdinalIgnoreCase);

        public Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null)
        {
            var node = new TestNode(name, attributes ?? new Dictionary<string, string>());
            _nodes[name] = node;
            return Task.FromResult<Node>(node);
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

        public Task Delete(string subNodeName, Node? parent = null)
        {
            _nodes.Remove(subNodeName);
            _connections.Remove(subNodeName);
            foreach (var connections in _connections.Values)
            {
                connections.RemoveAll(node => string.Equals(node.Name, subNodeName, StringComparison.OrdinalIgnoreCase));
            }

            return Task.CompletedTask;
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

        public Task<IReadOnlyCollection<Node>> GetAllNodesAsync()
        {
            return Task.FromResult<IReadOnlyCollection<Node>>(_nodes.Values.ToArray());
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
