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
using GraphData.Api.Services;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.SymLinkStorage;
using GraphData.Tests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SymLinkStorage;

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
        var result = await controller.GetNodeAsync([first.Name]);

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

        var result = await controller.GetNodeAsync(["missing"]);

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
        StringAssert.Contains(created.Location, "/api/graph/nodes?path=new%20node");

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("new node", response.Name);
        Assert.AreEqual("demo", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task CreateNodeAsync_CreatesChildWhenParentPathIsProvided()
    {
        await using var scope = TestGraphStorageScope.Create();
        var parent = await scope.Storage.Create("parent");
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest
        {
            Name = "child",
            ParentPath = [parent.Name]
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("parent/child", response.Name);

        var stored = await scope.Storage.Get("parent/child");
        Assert.IsNotNull(stored);
    }

    [TestMethod]
    public async Task GetNodeAsync_ResolvesChildByPathSegments()
    {
        await using var scope = TestGraphStorageScope.Create();
        var parent = await scope.Storage.Create("parent");
        var child = await scope.Storage.Create("child", parent);
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync(["parent", "child"]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(child.Name, response.Name);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForInvalidNodeName()
    {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest
        {
            Name = "KG Test: Ручное стрелковое оружие"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
        StringAssert.Contains(message, NodeNameValidator.AllowedSegmentCharactersDescription);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForReservedNodeNameSegment()
    {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest
        {
            Name = "CON"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "segment 'CON' is reserved");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsBadRequestForInvalidTargetName()
    {
        await using var scope = TestGraphStorageScope.Create();
        var source = await scope.Storage.Create("source");
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest
        {
            SourcePath = [source.Name],
            TargetPath = ["target:name"]
        });

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "Target node path segment contains invalid character ':'");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsNoContentForExistingHierarchyConnectionWithSymLinkStorage()
    {
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = await scope.Storage.Create("small_arms_test_graph");
        await scope.Storage.Create("weapons", root);
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest
        {
            SourcePath = ["small_arms_test_graph"],
            TargetPath = ["small_arms_test_graph", "weapons"]
        });

        Assert.IsInstanceOfType(result, typeof(NoContentResult));

        var rootPath = Path.Combine(scope.RootPath, "small_arms_test_graph");
        var childPath = Path.Combine(rootPath, "weapons");
        Assert.IsTrue(Directory.Exists(childPath));
        Assert.IsFalse(File.GetAttributes(childPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(rootPath)
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ConnectsNestedSiblingsWithSymLinkStorage()
    {
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = await scope.Storage.Create("small_arms_test_graph");
        var weapons = await scope.Storage.Create("weapons", root);
        var categories = await scope.Storage.Create("categories", root);
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest
        {
            SourcePath = ["small_arms_test_graph", "weapons"],
            TargetPath = ["small_arms_test_graph", "categories"]
        });

        Assert.IsInstanceOfType(result, typeof(NoContentResult));

        weapons = (await scope.Storage.Get("small_arms_test_graph/weapons"))!;
        categories = (await scope.Storage.Get("small_arms_test_graph/categories"))!;

        var weaponLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons", "categories");
        var categoryLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "categories", "weapons");
        Assert.IsTrue(File.GetAttributes(weaponLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(categoryLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "categories"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));

        var weaponConnections = await scope.Storage.GetConnectedNodesAsync(weapons);
        var categoryConnections = await scope.Storage.GetConnectedNodesAsync(categories);
        Assert.IsTrue(weaponConnections.Any(node => node.Name == categories.Name));
        Assert.IsTrue(categoryConnections.Any(node => node.Name == weapons.Name));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsInternalErrorDetailsWhenConnectFails()
    {
        await using var scope = TestGraphStorageScope.Create();
        var source = await scope.Storage.Create("source");
        var target = await scope.Storage.Create("target");
        var controller = CreateController(new ConnectThrowingGraphStorage(scope.Storage));

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest
        {
            SourcePath = [source.Name],
            TargetPath = [target.Name]
        });

        var objectResult = result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var message = objectResult.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "InvalidOperationException");
        StringAssert.Contains(message, "diagnostic connect failure");
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsBadRequestForInvalidLiteralNodeName()
    {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.SearchNodesAsync(new NodeSearchQuery
        {
            Where = new NodeExistsSearchExpression
            {
                Node = new NodeLiteralSearchSelector { Name = "bad:name" }
            }
        }, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
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
            RootPaths = [[first.Name]],
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
        var controller = new GraphController(
            new GraphApiService(new NodeService(storage), new GraphSearchService(storage)));
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

    private sealed class SymLinkGraphStorageScope : IAsyncDisposable
    {
        private readonly string _rootPath;

        private SymLinkGraphStorageScope(string rootPath)
        {
            _rootPath = rootPath;
            Storage = new SymLinkGraphStorage(
                Options.Create(new NtfsGraphStorageOptions { RootPath = rootPath }),
                new CancellationTokensAccessorMock(),
                NullLogger<SymLinkGraphStorage>.Instance);
        }

        public IGraphStorage Storage { get; }

        public string RootPath => _rootPath;

        public static SymLinkGraphStorageScope Create()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "GraphDataTests",
                "SymLinkApi",
                Guid.NewGuid().ToString("N"));
            return new SymLinkGraphStorageScope(rootPath);
        }

        public ValueTask DisposeAsync()
        {
            DeleteDirectoryWithoutFollowingLinks(new DirectoryInfo(_rootPath));
            return ValueTask.CompletedTask;
        }

        private static void DeleteDirectoryWithoutFollowingLinks(DirectoryInfo directory)
        {
            if (!directory.Exists)
            {
                return;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    entry.Delete();
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    DeleteDirectoryWithoutFollowingLinks(childDirectory);
                }
                else
                {
                    entry.Delete();
                }
            }

            directory.Delete();
        }
    }

    private sealed class ConnectThrowingGraphStorage(IGraphStorage inner) : IGraphStorage
    {
        public Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null) =>
            inner.Create(name, parent, attributes);

        public Task<Node?> Get(string basisNodeName) => inner.Get(basisNodeName);

        public Task<Node?> Get(Node? parent, string subNodeName) => inner.Get(parent, subNodeName);

        public Task<Node?> Get(NodeQuery query) => inner.Get(query);

        public Task Update(string subNodeName, IDictionary<string, string> attributes, Node? parent = null) =>
            inner.Update(subNodeName, attributes, parent);

        public Task Delete(string subNodeName, Node? parent = null) => inner.Delete(subNodeName, parent);

        public Task Connect(Node sourceNode, Node targetNode) =>
            throw new InvalidOperationException("diagnostic connect failure");

        public Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node) =>
            inner.GetConnectedNodesAsync(node);

        public Task<Subgraph> GetSubgraphAsync(SubgraphQuery query) => inner.GetSubgraphAsync(query);
    }
}
