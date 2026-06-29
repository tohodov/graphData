using System.Reflection;
using Abstractions;
using Domain.Services;
using GraphData.Core.Services;
using GraphData.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

[RelevantTestClass]
public abstract class StorageTests : IAsyncDisposable {
    internal static NtfsGraphStorageOptions StorageOptions = new NtfsGraphStorageOptions { //TODO спрятать
        RootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", Guid.NewGuid().ToString("N"))
    };
    readonly TestGraphStorageScope scope = new TestGraphStorageScope(StorageOptions);
    internal IGraphStorage Storage { get; }

    public StorageTests() {
        Storage = scope.Storage;
    }

    public virtual ValueTask DisposeAsync() => scope.DisposeAsync();
}
public abstract class GraphServiceTests : StorageTests {
    public GraphService Service { get; }
    public Graph Graph { get; private set; } = null!;
    protected virtual Assembly[] Assemblies { get; } = [];
    readonly GraphSchemaRegistry types;
    readonly GraphFactory graphFactory;
    GraphStorageInitializer? initializer = null;

    public GraphServiceTests() {
        types = GraphSchemaRegistry.Create(Assemblies);
        graphFactory = new GraphFactory(Storage, types);
        Service = new GraphService(
            Storage,
            new GraphSearchService(Storage),
            types);
    }

    [TestInitialize]
    public Task Init() {
        if (initializer != null)
            throw new Exception("неправильный жизненный цикл теста");
        initializer = new GraphStorageInitializer(graphFactory);
        return InitializeGraphAsync();
    }

    private async Task InitializeGraphAsync() {
        Graph = await initializer!.InitializeAsync().ConfigureAwait(false);
    }

    protected async Task AssertNode(NodeLocalId id, Node node) {
        Assert.AreEqual(id, node.LocalId);
        var storageNode = await Storage.Get(node.GlobalId);
        Assert.IsNotNull(storageNode);
        Assert.AreEqual(id, storageNode.LocalId);
    }
}
