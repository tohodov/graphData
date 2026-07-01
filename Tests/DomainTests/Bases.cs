using System.Reflection;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

[RelevantTestClass]
public abstract class StorageTests : IAsyncDisposable {
    internal static NtfsGraphStorageOptions StorageOptions = new NtfsGraphStorageOptions {
        RootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", Guid.NewGuid().ToString("N"))
    };
    internal IGraphStorage Storage { get; }

    [TestInitialize]
    public virtual Task Init() {
        return Task.CompletedTask;
    }

    public StorageTests() {
        Storage = new SymLinkGraphStorage(Options.Create(StorageOptions), new CancellationTokensAccessorMock());
    }

    public virtual ValueTask DisposeAsync() {
        if (!string.IsNullOrWhiteSpace(StorageOptions.RootPath) && Directory.Exists(StorageOptions.RootPath))
            Directory.Delete(StorageOptions.RootPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    protected virtual async Task AssertNode(NodeLocalId id, Node node) {
        Assert.AreEqual(id, node.LocalId);
        var storageNode = await Storage.Get(node.GlobalId);
        Assert.IsNotNull(storageNode);
        Assert.AreEqual(id, storageNode.LocalId);
    }
}
public abstract class GraphServiceTests : StorageTests {
    public GraphService Service { get; }
    protected virtual Assembly[] Assemblies { get; } = [];

    public GraphServiceTests() {
        Service = new GraphService(Storage, new GraphSearchService(Storage), GraphSchemaRegistry.Create(Assemblies));
    }

    [TestInitialize]
    public override async Task Init() {
        await new GraphFactory(Storage, GraphSchemaRegistry.Create(Assemblies)).OpenAsync();
    }

    protected async Task<Subgraph> GetRoots() {
        return (await Service.GetSubgraph([], 0)).Value!;
    }
    protected async Task<Node> GetTypesRoot() {
        var result = await Service.GetNode(FixedGraphTopology.NodeTypesId);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        Assert.IsNotNull(result.Value);
        return result.Value;
    }
    protected async Task<Node> Create(string localId) {
        var result = await Service.CreateNode(localId);
        Assert.IsNotNull(result.Value);
        await AssertNode(localId, result.Value);
        return result.Value;
    }
    protected async Task<Node> Create(string localId, NodeRef parent) {
        var result = await Service.CreateNode(localId, parent);
        Assert.IsNotNull(result.Value);
        await AssertNode(localId, result.Value);
        return result.Value;
    }
}
public abstract class GraphDslTests : StorageTests {
    public Graph Graph { get; private set; } = null!;
    protected virtual Assembly[] Assemblies { get; } = [];

    [TestInitialize]
    public override async Task Init() {
        if (Graph != null)
            throw new Exception("неправильный жизненный цикл теста");
        Graph = await new GraphFactory(Storage, GraphSchemaRegistry.Create(Assemblies)).OpenAsync();
    }
}
public abstract class GraphTests : GraphDslTests {
    public GraphService Service { get; }

    public GraphTests() {
        Service = new GraphService(Storage, new GraphSearchService(Storage), GraphSchemaRegistry.Create(Assemblies));
    }
}
