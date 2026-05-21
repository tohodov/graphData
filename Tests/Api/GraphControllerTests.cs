using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Tests;
using Microsoft.AspNetCore.Http;
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
        var matches = await SearchNodesAsync(controller, new NodeSearchQuery
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

        var match = matches.Single();
        Assert.AreEqual(first.Name, match.Bindings["n"].Name);
        Assert.AreEqual(second.Name, match.Bindings["x"].Name);
    }

    [TestMethod]
    public async Task SearchNodesAsync_WritesNdjsonMatches()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = await scope.Storage.Create("1");
        var second = await scope.Storage.Create("2", attributes: new Dictionary<string, string> { ["id"] = "Y" });
        await scope.Storage.Connect(first, second);

        var controller = CreateController(scope.Storage);
        var matches = await SearchNodesAsync(controller, new NodeSearchQuery
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
                "expressions": [
                  {
                    "left": { "kind": "var", "name": "n" },
                    "right": { "kind": "var", "name": "x" },
                    "kind": "connected"
                  },
                  {
                    "node": { "kind": "var", "name": "x" },
                    "key": "id",
                    "operator": "equals",
                    "value": "Y",
                    "kind": "attribute"
                  }
                ],
                "kind": "all"
              },
              "orderBy": [
                {
                  "variable": "n",
                  "direction": "descending",
                  "kind": "degree"
                }
              ],
              "limit": 20
            }
            """;

        var query = JsonSerializer.Deserialize<NodeSearchQuery>(
            json,
            GraphJsonSerializerOptions.Create());

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
        var controller = new GraphController(new NodeService(storage), new GraphSearchService(storage));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatchResponse>> SearchNodesAsync(
        GraphController controller,
        NodeSearchQuery query)
    {
        var result = await controller.SearchNodesAsync(query, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(EmptyResult));
        StringAssert.Contains(controller.Response.ContentType, "application/x-ndjson");

        controller.Response.Body.Position = 0;
        using var reader = new StreamReader(controller.Response.Body, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync();

        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<NodeSearchMatchResponse>(
                line,
                GraphJsonSerializerOptions.Create()))
            .Cast<NodeSearchMatchResponse>()
            .ToArray();
    }
}
