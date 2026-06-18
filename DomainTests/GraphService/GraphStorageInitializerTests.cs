using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

namespace GraphData.Tests.Storage;

[RelevantTestClass]
public sealed class GraphStorageInitializerTests
{
    [TestMethod]
    public async Task InitializeAsync_CreatesRuntimeTypeSubgraphsAndCompletionMarker()
    {
        await using var scope = TestGraphStorageScope.Create();
        var initializer = new GraphStorageInitializer(scope.Storage);

        await initializer.InitializeAsync();

        var nodeTypeRoot = await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.NodeTypeRoot);
        Assert.AreEqual("type-root", nodeTypeRoot.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual("node", nodeTypeRoot.Attributes[GraphRuntimeAttributeNames.GraphElement]);

        var edgeTypeRoot = await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.EdgeTypeRoot);
        Assert.AreEqual("type-root", edgeTypeRoot.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual("edge", edgeTypeRoot.Attributes[GraphRuntimeAttributeNames.GraphElement]);

        var relationRoot = await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.RelationRoot);
        Assert.AreEqual("relation-root", relationRoot.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual("edge", relationRoot.Attributes[GraphRuntimeAttributeNames.GraphElement]);

        AssertBaseType(
            await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.NodeType),
            "node",
            "Type",
            "#334155",
            "90");
        AssertBaseType(
            await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.NodeInstance),
            "node",
            "Instance",
            "#0f766e",
            "70");
        AssertBaseType(
            await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.EdgeType),
            "edge",
            "Type",
            "#7c2d12",
            "60",
            directed: "true");
        AssertBaseType(
            await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.EdgeInstance),
            "edge",
            "Instance",
            "#b45309",
            "50",
            directed: "true");

        var marker = await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.RuntimeTypesInitializer);
        Assert.AreEqual("storage-initializer", marker.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual("runtime-types", marker.Attributes["storage.initializer"]);
        Assert.AreEqual(bool.TrueString, marker.Attributes["completed"]);
        Assert.AreEqual("1", marker.Attributes["storage.initializer.version"]);

        var subgraph = (await scope.Storage.GetSubgraphAsync(new SubgraphQuery {
            Nodes = [GraphSystemNodeIds.NodeTypeRoot, GraphSystemNodeIds.EdgeTypeRoot],
            MaxDepth = 4
        })).Value!;
        var subgraphIds = subgraph.Nodes.Select(static node => node.GlobalId).ToArray();

        CollectionAssert.IsSubsetOf(
            new[] {
                GraphBaseTypeIds.NodeType,
                GraphBaseTypeIds.NodeInstance,
                GraphBaseTypeIds.EdgeType,
                GraphBaseTypeIds.EdgeInstance
            },
            subgraphIds);
    }

    [TestMethod]
    public async Task InitializeAsync_PreservesExistingTypeAttributesBeforeCompletion()
    {
        await using var scope = TestGraphStorageScope.Create();
        await CreatePathAsync(
            scope.Storage,
            GraphBaseTypeIds.NodeInstance,
            new Dictionary<string, string> {
                ["color"] = "#123456"
            });

        var initializer = new GraphStorageInitializer(scope.Storage);
        await initializer.InitializeAsync();

        var node = await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.NodeInstance);
        Assert.AreEqual("#123456", node.Attributes["color"]);
        Assert.AreEqual("type", node.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual("node", node.Attributes[GraphRuntimeAttributeNames.GraphElement]);
        Assert.AreEqual("Instance", node.Attributes["label"]);
        Assert.AreEqual("70", node.Attributes[GraphRuntimeAttributeNames.ProjectionRank]);
    }

    [TestMethod]
    public async Task InitializeAsync_SkipsAfterCompletionFlagFromPreviousStorageInstance()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "GraphDataTests",
            "Initializer",
            Guid.NewGuid().ToString("N"));
        try {
            var firstStorage = CreateStorage(rootPath);
            await new GraphStorageInitializer(firstStorage).InitializeAsync();

            var firstInstance = await GetRequiredAsync(firstStorage, GraphBaseTypeIds.NodeInstance);
            var attributes = new Dictionary<string, string>(firstInstance.Attributes, StringComparer.OrdinalIgnoreCase) {
                ["color"] = "#abcdef"
            };
            await firstStorage.Update(GraphBaseTypeIds.NodeInstance, attributes);

            var secondStorage = CreateStorage(rootPath);
            await new GraphStorageInitializer(secondStorage).InitializeAsync();

            var secondInstance = await GetRequiredAsync(secondStorage, GraphBaseTypeIds.NodeInstance);
            Assert.AreEqual("#abcdef", secondInstance.Attributes["color"]);

            var marker = await GetRequiredAsync(secondStorage, GraphSystemNodeIds.RuntimeTypesInitializer);
            Assert.AreEqual(bool.TrueString, marker.Attributes["completed"]);
        } finally {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private static IGraphStorage CreateStorage(string rootPath) =>
        new SymLinkGraphStorage(
            Options.Create(new NtfsGraphStorageOptions { RootPath = rootPath }),
            new CancellationTokensAccessorMock(),
            NullLogger<SymLinkGraphStorage>.Instance);

    private static async Task CreatePathAsync(
        IGraphStorage storage,
        NodeGlobalId id,
        IDictionary<string, string>? attributes = null)
    {
        var segments = id.ToArray();
        NodeGlobalId? parent = null;
        for (var index = 0; index < segments.Length; index++) {
            var current = new NodeGlobalId(segments.Take(index + 1));
            var existing = await storage.Get(current);
            if (existing.Status == ServiceResultStatus.NotFound) {
                NodePath? parentPath = parent is null ? null : parent.Value;
                var create = await storage.Create(
                    segments[index],
                    parentPath,
                    index == segments.Length - 1 ? attributes : null);
                Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
            }

            parent = current;
        }
    }

    private static async Task<NodeState> GetRequiredAsync(IGraphStorage storage, NodeGlobalId id)
    {
        var result = await storage.Get(id);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        Assert.IsNotNull(result.Value);
        return result.Value;
    }

    private static void AssertBaseType(
        NodeState node,
        string element,
        string label,
        string color,
        string rank,
        string? directed = null)
    {
        Assert.AreEqual("type", node.Attributes[GraphRuntimeAttributeNames.GraphKind]);
        Assert.AreEqual(element, node.Attributes[GraphRuntimeAttributeNames.GraphElement]);
        Assert.AreEqual(label, node.Attributes["label"]);
        Assert.AreEqual(color, node.Attributes["color"]);
        Assert.AreEqual(rank, node.Attributes[GraphRuntimeAttributeNames.ProjectionRank]);
        if (directed is not null)
            Assert.AreEqual(directed, node.Attributes["directed"]);
    }
}
