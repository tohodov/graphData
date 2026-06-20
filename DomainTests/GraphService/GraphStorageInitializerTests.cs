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
        var service = new GraphData.Core.Services.GraphService(scope.Storage, new GraphSearchService(scope.Storage), new CancellationTokensAccessorMock());
        var initializer = new GraphStorageInitializer(scope.Storage);

        await initializer.InitializeAsync();

        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.NodeTypeRoot));
        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.NodeType));
        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.NodeInstance));
        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.Relation));
        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.Endpoint));
        AssertNoAttributes(await GetRequiredAsync(scope.Storage, GraphBaseTypeIds.Port));
        await AssertConnectedAsync(scope.Storage, GraphBaseTypeIds.NodeInstance, GraphBaseTypeIds.NodeType);
        await AssertConnectedAsync(scope.Storage, GraphBaseTypeIds.Relation, GraphBaseTypeIds.NodeType);
        await AssertConnectedAsync(scope.Storage, GraphBaseTypeIds.Endpoint, GraphBaseTypeIds.NodeType);
        await AssertConnectedAsync(scope.Storage, GraphBaseTypeIds.Port, GraphBaseTypeIds.NodeType);

        var marker = await GetRequiredAsync(scope.Storage, GraphSystemNodeIds.RuntimeTypesInitializer);
        AssertNoAttributes(marker);
        await AssertHasCompletionMarkerAsync(service);

        var subgraph = (await service.GetSubgraph([GraphSystemNodeIds.NodeTypeRoot], 4)).Value!;
        var subgraphIds = subgraph.Nodes.Select(static node => node.GlobalId).ToArray();

        CollectionAssert.IsSubsetOf(
            new[] {
                GraphBaseTypeIds.NodeType,
                GraphBaseTypeIds.NodeInstance,
                GraphBaseTypeIds.Relation,
                GraphBaseTypeIds.Endpoint,
                GraphBaseTypeIds.Port
            },
            subgraphIds);
    }

    [TestMethod]
    public async Task InitializeAsync_RegistersNodeTypeDescriptorsFromAdditionalAssemblies()
    {
        await using var scope = TestGraphStorageScope.Create();
        var initializer = new GraphStorageInitializer(scope.Storage, typeof(CatalogWeaponNodeType).Assembly);

        await initializer.InitializeAsync();

        var weapon = await GetRequiredAsync(scope.Storage, TypeId<CatalogWeaponNodeType>());
        AssertNoAttributes(weapon);
        await AssertConnectedAsync(scope.Storage, weapon.GlobalId, GraphBaseTypeIds.NodeType);
    }

    [TestMethod]
    public async Task InitializeAsync_ReplaysWhenRuntimeTypeCatalogChanges()
    {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage).InitializeAsync();

        await new GraphStorageInitializer(scope.Storage, typeof(CatalogWeaponNodeType).Assembly).InitializeAsync();

        var weapon = await GetRequiredAsync(scope.Storage, TypeId<CatalogWeaponNodeType>());
        AssertNoAttributes(weapon);
        await AssertConnectedAsync(scope.Storage, weapon.GlobalId, GraphBaseTypeIds.NodeType);
    }

    [TestMethod]
    public async Task InitializeAsync_PreservesExistingUserAttributesBeforeCompletion()
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
        Assert.AreEqual(1, node.Attributes.Count);
    }

    [TestMethod]
    public async Task InitializeAsync_SkipsAfterCompletionMarkerFromPreviousStorageInstance()
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

            await AssertHasCompletionMarkerAsync(new (secondStorage, new GraphSearchService(secondStorage), new CancellationTokensAccessorMock()));
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
        InternalId id,
        IDictionary<string, string>? attributes = null)
    {
        var segments = id.ToArray();
        NodePath? parent = null;
        for (var index = 0; index < segments.Length; index++) {
            var current = new InternalId(segments.Take(index + 1));
            var existing = await storage.Get(current);
            if (existing.Status == ServiceResultStatus.NotFound) {
                NodePath? parentPath = parent is null ? null : parent;
                var create = await storage.Create(
                    segments[index],
                    parentPath,
                    index == segments.Length - 1 ? attributes : null);
                Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
            }

            parent = current;
        }
    }

    private static async Task<NodeState> GetRequiredAsync(IGraphStorage storage, InternalId id)
    {
        var result = await storage.Get(id);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        Assert.IsNotNull(result.Value);
        return result.Value;
    }

    private static async Task AssertConnectedAsync(IGraphStorage storage, InternalId sourceId, InternalId targetId)
    {
        var source = await GetRequiredAsync(storage, sourceId);
        var connected = (await storage.GetConnectedNodesAsync(source)).Value!;
        Assert.IsTrue(
            connected.Any(node => node.GlobalId == targetId),
            $"Expected '{sourceId}' to be connected to '{targetId}'.");
    }

    private static async Task AssertHasCompletionMarkerAsync(GraphData.Core.Services.GraphService service)
    {
        var subgraph = (await service.GetSubgraph([GraphSystemNodeIds.RuntimeTypesInitializer], 2)).Value!;
        Assert.IsTrue(
            subgraph.Nodes.Any(node => node.GlobalId.ToString().StartsWith($"{GraphSystemNodeIds.RuntimeTypesInitializer}/5/", StringComparison.Ordinal)),
            "Expected runtime type initializer completion marker node.");
    }

    private static void AssertNoAttributes(NodeState node) =>
        Assert.AreEqual(0, node.Attributes.Count, $"Expected '{node.GlobalId}' to have no generated attributes.");

    private static InternalId TypeId<TNodeType>()
    {
        var name = typeof(TNodeType).Name;
        if (name.EndsWith(nameof(NodeType), StringComparison.Ordinal))
            name = name[..^nameof(NodeType).Length];
        return new InternalId("graphdata", "types", "nodes", name);
    }

    private sealed class CatalogWeaponNodeType : NodeType
    {
    }
}
