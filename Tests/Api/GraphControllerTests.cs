using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[TestClass]
public sealed class GraphControllerTests
{
    [TestMethod]
    public async Task GetNodeAsync_ReturnsNodeWithNeighborEdges()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = await scope.Storage.Create("1", attributes: new Dictionary<string, string> { ["kind"] = "root" });
        var second = await scope.Storage.Create("2", attributes: new Dictionary<string, string> { ["kind"] = "leaf" });
        await scope.Storage.Connect(first, second);

        var controller = CreateController(scope.Storage);
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
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync("missing");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsCreatedNodeWithLocation()
    {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

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
        await using var scope = TestGraphStorageScope.Create();
        var first = await scope.Storage.Create("1");
        var second = await scope.Storage.Create("2");
        var third = await scope.Storage.Create("3");
        await scope.Storage.Connect(first, second);
        await scope.Storage.Connect(second, third);

        var controller = CreateController(scope.Storage);
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
        await using var scope = TestGraphStorageScope.Create();
        var first = await scope.Storage.Create("1", attributes: new Dictionary<string, string> { ["id"] = "source" });
        var second = await scope.Storage.Create("2", attributes: new Dictionary<string, string> { ["id"] = "Y" });
        await scope.Storage.Connect(first, second);

        var controller = CreateController(scope.Storage);
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
    public async Task SearchNodesStreamAsync_ReturnsAsyncEnumerableBindings()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = await scope.Storage.Create("1");
        var second = await scope.Storage.Create("2", attributes: new Dictionary<string, string> { ["id"] = "Y" });
        await scope.Storage.Connect(first, second);

        var controller = CreateController(scope.Storage);
        var result = controller.SearchNodesStreamAsync(
            new NodeSearchQuery
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
            },
            CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var stream = ok.Value as IAsyncEnumerable<NodeSearchMatchResponse>;
        Assert.IsNotNull(stream);

        var matches = new List<NodeSearchMatchResponse>();
        await foreach (var match in stream)
        {
            matches.Add(match);
        }

        var streamed = matches.Single();
        Assert.AreEqual(first.Name, streamed.Bindings["n"].Name);
        Assert.AreEqual(second.Name, streamed.Bindings["x"].Name);
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
              "orderBy": [
                {
                  "kind": "degree",
                  "variable": "n",
                  "direction": "descending"
                }
              ],
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
        Assert.IsInstanceOfType(query.OrderBy.Single(), typeof(NodeSearchDegreeOrder));
    }

    private static GraphController CreateController(IGraphStorage storage)
    {
        return new GraphController(new NodeService(storage), new GraphSearchService(storage));
    }
}
