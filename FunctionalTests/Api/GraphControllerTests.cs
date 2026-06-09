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
public sealed class GraphControllerTests {
    [TestMethod]
    public void ConnectNodesRequest_DeserializesJsonGlobalIdArrays() {
        var request = JsonSerializer.Deserialize<ConnectNodesRequest>(
            """
            {
              "sourceGlobalId": [ "Small_Arms_Web_KG", "Weapons", "AK_47" ],
              "targetGlobalId": [ "Small_Arms_Web_KG", "Categories", "Assault_Rifle" ]
            }
            """,
            GraphJsonSerializerOptions.Create());

        Assert.IsNotNull(request);
        Assert.AreEqual("Small_Arms_Web_KG/Weapons/AK_47", request.SourceGlobalId.ToString());
        Assert.AreEqual("Small_Arms_Web_KG/Categories/Assault_Rifle", request.TargetGlobalId.ToString());
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNodeWithNeighborEdges() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["kind"] = "root" })).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["kind"] = "leaf" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetNodeAsync([first.LocalId]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(first.LocalId.ToString(), response.LocalId);
        Assert.AreEqual(first.GlobalId.ToString(), response.GlobalId);
        Assert.AreEqual("root", response.Attributes["kind"]);

        var edge = response.Edges.Single();
        Assert.AreEqual(second.LocalId.ToString(), edge.NeighborLocalId);
        Assert.AreEqual(first.LocalId.ToString(), edge.SourceLocalId);
        Assert.AreEqual(second.LocalId.ToString(), edge.TargetLocalId);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsNeighborByLocalId() {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var actions = (await scope.Storage.Create(new("actions"), root.GlobalId, new Dictionary<string, string> { ["kind"] = "child" })).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(root.GlobalId.ToString(), actions.LocalId.ToString());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(actions.GlobalId.ToString(), response.GlobalId);
        Assert.AreEqual(actions.LocalId.ToString(), response.LocalId);
        Assert.AreEqual("child", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_DecodesNestedGlobalIdRouteSegment() {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("Small_Arms_Web_KG"))).Value!;
        var actions = (await scope.Storage.Create(new("Actions"), root.GlobalId)).Value!;
        var gasOperated = (await scope.Storage.Create(new("Gas_Operated"), actions.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(
            Uri.EscapeDataString(actions.GlobalId.ToString()),
            gasOperated.LocalId.ToString());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(gasOperated.GlobalId.ToString(), response.GlobalId);
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsNotFoundForMissingNeighbor() {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNeighborNodeAsync(root.GlobalId.ToString(), "missing");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task GetNeighborNodeAsync_ReturnsConflictForAmbiguousLocalId() {
        var controller = CreateController(AmbiguousNeighborGraphStorage.Create());

        var result = await controller.GetNeighborNodeAsync("root", "same");

        var conflict = result.Result as ConflictObjectResult;
        Assert.IsNotNull(conflict);
        StringAssert.Contains(conflict.Value?.ToString(), "same");
    }

    [TestMethod]
    public async Task GetNodeAsync_ReturnsNotFoundForMissingNode() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync(["missing"]);

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundResult));
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsCreatedNodeWithLocation() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "new node",
            Attributes = new Dictionary<string, string> { ["kind"] = "demo" }
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        StringAssert.Contains(created.Location, "/api/graph/nodes?globalId=new%20node");

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("new node", response.LocalId);
        Assert.AreEqual("new node", response.GlobalId);
        Assert.AreEqual("demo", response.Attributes["kind"]);
    }

    [TestMethod]
    public async Task CreateNodeAsync_CreatesChildWhenParentGlobalIdIsProvided() {
        await using var scope = TestGraphStorageScope.Create();
        var parent = (await scope.Storage.Create(new("parent"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "child",
            ParentGlobalId = [parent.LocalId]
        });

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);

        var response = created.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual("child", response.LocalId);
        Assert.AreEqual("parent/child", response.GlobalId);

        var stored = await scope.Storage.Get(new("parent","child"));
        Assert.IsNotNull(stored);
    }

    [TestMethod]
    public async Task GetNodeAsync_ResolvesChildByGlobalIdSegments() {
        await using var scope = TestGraphStorageScope.Create();
        var parent = (await scope.Storage.Create(new("parent"))).Value!;
        var child = (await scope.Storage.Create(new("child"), parent.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.GetNodeAsync(["parent", "child"]);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var response = ok.Value as NodeResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(child.LocalId.ToString(), response.LocalId);
        Assert.AreEqual(child.GlobalId.ToString(), response.GlobalId);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForInvalidNodeName() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "KG Test: Ручное стрелковое оружие"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
        StringAssert.Contains(message, NodeNameValidator.AllowedSegmentCharactersDescription);
    }

    [TestMethod]
    public async Task CreateNodeAsync_ReturnsBadRequestForReservedNodeNameSegment() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.CreateNodeAsync(new CreateNodeRequest {
            LocalId = "CON"
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "segment 'CON' is reserved");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsBadRequestForInvalidTargetName() {
        await using var scope = TestGraphStorageScope.Create();
        var source = (await scope.Storage.Create(new("source"))).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            SourceGlobalId = new([source.LocalId]),
            TargetGlobalId = new(["target:name"])
        });

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "Target node path segment contains invalid character ':'");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsNoContentForExistingHierarchyConnectionWithSymLinkStorage() {
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("small_arms_test_graph"))).Value!;
        await scope.Storage.Create(new("weapons"), root.GlobalId);
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            SourceGlobalId = new(["small_arms_test_graph"]),
            TargetGlobalId = new(["small_arms_test_graph", "weapons"])
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        var rootPath = Path.Combine(scope.RootPath, "small_arms_test_graph");
        var childPath = Path.Combine(rootPath, "weapons");
        Assert.IsTrue(Directory.Exists(childPath));
        Assert.IsFalse(File.GetAttributes(childPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(rootPath)
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ConnectsNestedSiblingsWithSymLinkStorage() {
        await using var scope = SymLinkGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("small_arms_test_graph"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var categories = (await scope.Storage.Create(new("categories"), root.GlobalId)).Value!;
        var controller = CreateController(scope.Storage);

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            SourceGlobalId = new(["small_arms_test_graph", "weapons"]),
            TargetGlobalId = new(["small_arms_test_graph", "categories"])
        });

        Assert.IsInstanceOfType(result.Result, typeof(NoContentResult));

        weapons = (await scope.Storage.Get(new("small_arms_test_graph", "weapons"))).Value!;
        categories = (await scope.Storage.Get(new("small_arms_test_graph", "categories"))).Value!;

        var weaponLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons", "categories");
        var categoryLinkPath = Path.Combine(scope.RootPath, "small_arms_test_graph", "categories", "weapons");
        Assert.IsTrue(File.GetAttributes(weaponLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(File.GetAttributes(categoryLinkPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "weapons"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(scope.RootPath, "small_arms_test_graph", "categories"))
            .Any(path => Path.GetFileName(path).StartsWith(".graphdata-node-", StringComparison.OrdinalIgnoreCase)));

        var weaponConnections = (await scope.Storage.GetConnectedNodesAsync(weapons)).Value!;
        var categoryConnections = (await scope.Storage.GetConnectedNodesAsync(categories)).Value!;
        Assert.IsTrue(weaponConnections.Any(node => node.LocalId == categories.LocalId));
        Assert.IsTrue(categoryConnections.Any(node => node.LocalId == weapons.LocalId));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ReturnsInternalErrorDetailsWhenConnectFails() {
        await using var scope = TestGraphStorageScope.Create();
        var source = (await scope.Storage.Create(new("source"))).Value!;
        var target = (await scope.Storage.Create(new("target"))).Value!;
        var controller = CreateController(new ConnectThrowingGraphStorage(scope.Storage));

        var result = await controller.ConnectNodesAsync(new ConnectNodesRequest {
            SourceGlobalId = new([source.LocalId]),
            TargetGlobalId = new([target.LocalId])
        });

        var objectResult = result.Result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var message = objectResult.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "InvalidOperationException");
        StringAssert.Contains(message, "diagnostic connect failure");
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsBadRequestForInvalidLiteralNodeName() {
        await using var scope = TestGraphStorageScope.Create();
        var controller = CreateController(scope.Storage);

        var result = await controller.SearchNodesAsync(new NodeSearchQuery {
            Where = new NodeExistsSearchExpression {
                Node = new NodeLiteralSearchSelector { Name = "bad:name" }
            }
        }, CancellationToken.None);

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        var message = badRequest.Value as string;
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "invalid character ':'");
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsTopLevelEdgesWithoutDuplicatingThemOnNodes() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"))).Value!;
        var second = (await scope.Storage.Create(new("2"))).Value!;
        var third = (await scope.Storage.Create(new("3"))).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);
        await scope.Storage.Connect(second.GlobalId, third.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
            GlobalIds = [[first.LocalId]],
            MaxDepth = 1
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(new[] { "1", "2" }, response.Nodes.Select(static node => node.LocalId).ToArray());
        Assert.IsTrue(response.Nodes.All(static node => node.Edges.Count == 0));

        var edge = response.Edges.Single();
        Assert.AreEqual(first.LocalId.ToString(), edge.SourceLocalId);
        Assert.AreEqual(second.LocalId.ToString(), edge.TargetLocalId);
    }

    [TestMethod]
    public async Task GetSubgraphAsync_ReturnsNestedNeighborEdgesWithGlobalIds() {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var categories = (await scope.Storage.Create(new("categories"), root.GlobalId)).Value!;
        var ak47 = (await scope.Storage.Create(new("ak_47"), weapons.GlobalId)).Value!;
        var assaultRifle = (await scope.Storage.Create(new("assault_rifle"), categories.GlobalId)).Value!;
        await scope.Storage.Connect(ak47.GlobalId, assaultRifle.GlobalId);

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
            GlobalIds = [["root", "weapons", "ak_47"]],
            MaxDepth = 1
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString(), weapons.GlobalId.ToString() },
            response.Nodes.Select(static node => node.GlobalId).ToArray());
        Assert.IsTrue(response.Nodes.All(static node => node.Edges.Count == 0));

        Assert.AreEqual(2, response.Edges.Count);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, ak47.GlobalId.ToString(), assaultRifle.GlobalId.ToString())));
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, weapons.GlobalId.ToString(), ak47.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task GetSubgraphAsync_TraverseHierarchyEdges() {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create(new("root"))).Value!;
        var weapons = (await scope.Storage.Create(new("weapons"), root.GlobalId)).Value!;
        var ak47 = (await scope.Storage.Create(new("ak_47"), weapons.GlobalId)).Value!;

        var controller = CreateController(scope.Storage);
        var result = await controller.GetSubgraphAsync(new SubgraphRequest {
            GlobalIds = [["root"]],
            MaxDepth = 2
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);

        var response = ok.Value as SubgraphResponse;
        Assert.IsNotNull(response);
        CollectionAssert.AreEquivalent(
            new[] { root.GlobalId.ToString(), weapons.GlobalId.ToString(), ak47.GlobalId.ToString() },
            response.Nodes.Select(static node => node.GlobalId).ToArray());
        Assert.AreEqual(2, response.Edges.Count);
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, root.GlobalId.ToString(), weapons.GlobalId.ToString())));
        Assert.IsTrue(response.Edges.Any(edge => HasEndpoints(edge, weapons.GlobalId.ToString(), ak47.GlobalId.ToString())));
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsVariableBindings() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"), attributes: new Dictionary<string, string> { ["id"] = "source" })).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var matches = await SearchNodesAsync(controller, new NodeSearchQuery {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpression {
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
        Assert.AreEqual(first.LocalId.ToString(), match.Bindings["n"].LocalId);
        Assert.AreEqual(second.LocalId.ToString(), match.Bindings["x"].LocalId);
    }

    [TestMethod]
    public async Task SearchNodesAsync_WritesNdjsonMatches() {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create(new("1"))).Value!;
        var second = (await scope.Storage.Create(new("2"), attributes: new Dictionary<string, string> { ["id"] = "Y" })).Value!;
        await scope.Storage.Connect(first.GlobalId, second.GlobalId);

        var controller = CreateController(scope.Storage);
        var matches = await SearchNodesAsync(controller, new NodeSearchQuery {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpression {
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
        Assert.AreEqual(first.LocalId.ToString(), streamed.Bindings["n"].LocalId);
        Assert.AreEqual(second.LocalId.ToString(), streamed.Bindings["x"].LocalId);
    }

    private static GraphController CreateController(IGraphStorage storage) {
        var controller = new GraphController(storage, new GraphSearchService(storage));
        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatchResponse>> SearchNodesAsync(
        GraphController controller,
        NodeSearchQuery query) {
        var result = await controller.SearchNodesAsync(query, CancellationToken.None);

        Assert.IsInstanceOfType(result.Result, typeof(EmptyResult));
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

    private static bool HasEndpoints(EdgeResponse edge, string left, string right) =>
        (edge.SourceGlobalId == left && edge.TargetGlobalId == right) ||
        (edge.SourceGlobalId == right && edge.TargetGlobalId == left);

    private sealed class SymLinkGraphStorageScope : IAsyncDisposable {
        private readonly string _rootPath;

        private SymLinkGraphStorageScope(string rootPath) {
            _rootPath = rootPath;
            Storage = new SymLinkGraphStorage(
                Options.Create(new NtfsGraphStorageOptions { RootPath = rootPath }),
                new CancellationTokensAccessorMock(),
                NullLogger<SymLinkGraphStorage>.Instance);
        }

        public IGraphStorage Storage { get; }

        public string RootPath => _rootPath;

        public static SymLinkGraphStorageScope Create() {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "GraphDataTests",
                "SymLinkApi",
                Guid.NewGuid().ToString("N"));
            return new SymLinkGraphStorageScope(rootPath);
        }

        public ValueTask DisposeAsync() {
            DeleteDirectoryWithoutFollowingLinks(new DirectoryInfo(_rootPath));
            return ValueTask.CompletedTask;
        }

        private static void DeleteDirectoryWithoutFollowingLinks(DirectoryInfo directory) {
            if (!directory.Exists) {
                return;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos()) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    entry.Delete();
                    continue;
                }

                if (entry is DirectoryInfo childDirectory) {
                    DeleteDirectoryWithoutFollowingLinks(childDirectory);
                } else {
                    entry.Delete();
                }
            }

            directory.Delete();
        }
    }

    private sealed class ConnectThrowingGraphStorage(IGraphStorage inner) : IGraphStorage {
        public Task<ServiceResult<Node>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null) =>
            inner.Create(name, parent, attributes);

        public Task<ServiceResult<Node>> Get(NodeGlobalId query) => inner.Get(query);

        public Task<ServiceResult> Delete(NodeGlobalId query) => inner.Delete(query);

        public Task<ServiceResult> Connect(NodeGlobalId sourcePath, NodeGlobalId targetPath) =>
            Task.FromResult(ServiceResult.InternalServerError(new InvalidOperationException("diagnostic connect failure").ToString()));

        public Task<ServiceResult> Disconnect(NodeGlobalId sourcePath, NodeGlobalId targetPath) =>
            Task.FromResult(ServiceResult.InternalServerError(new InvalidOperationException("diagnostic connect failure").ToString()));

        public Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(Node node) =>
            inner.GetConnectedNodesAsync(node);
    }

    private sealed class AmbiguousNeighborGraphStorage : IGraphStorage {
        private readonly StaticNode _root;

        private AmbiguousNeighborGraphStorage(StaticNode root) {
            _root = root;
        }

        public static IGraphStorage Create() {
            var root = new StaticNode(new("root"), new("root"));
            var first = new StaticNode(new("same"), new("left", "same"));
            var second = new StaticNode(new("same"), new("right", "same"));
            root.EdgeSnapshot = [
                new StaticEdge(root, first),
                new StaticEdge(root, second)
            ];

            return new AmbiguousNeighborGraphStorage(root);
        }

        public Task<ServiceResult<Node>> Get(NodeGlobalId path) =>
            Task.FromResult(path == _root.GlobalId
                ? ServiceResult<Node>.Ok(_root)
                : ServiceResult<Node>.NotFound());

        public Task<ServiceResult<Node>> Create(NodeLocalId name, NodeGlobalId? parent = null, IDictionary<string, string>? attributes = null) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Delete(NodeGlobalId path) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Connect(NodeGlobalId sourcePath, NodeGlobalId targetPath) =>
            throw new NotSupportedException();

        public Task<ServiceResult> Disconnect(NodeGlobalId sourcePath, NodeGlobalId targetPath) =>
            throw new NotSupportedException();

        public Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(Node node) =>
            throw new NotSupportedException();
    }

    private sealed class StaticNode(NodeLocalId localId, NodeGlobalId globalId) : Node {
        public override NodeLocalId LocalId { get; } = localId;

        public override NodeGlobalId GlobalId { get; } = globalId;

        public ICollection<Edge> EdgeSnapshot { get; set; } = Array.Empty<Edge>();

        public override ICollection<Edge> Edges => EdgeSnapshot;

        public override ICollection<Node> Nodes => Edges
            .SelectMany(static edge => new[] { edge.Node1, edge.Node2 })
            .Where(node => node.GlobalId != GlobalId)
            .ToArray();

        public override IDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StaticEdge(Node First, Node Second) : Edge {
        public override Node Node1 => First;

        public override Node Node2 => Second;
    }
}
