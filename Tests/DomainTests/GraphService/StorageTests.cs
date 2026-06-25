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

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
public abstract class GraphServiceTests : StorageTests {
    public GraphService Service { get; }
    protected virtual Assembly[] Assemblies { get; } = [];
    readonly GraphSchemaRegistry types;
    GraphStorageInitializer? initializer = null;

    public GraphServiceTests() {
        types = GraphSchemaRegistry.Create(Assemblies);
        Service = new GraphService(
            Storage,
            new GraphSearchService(Storage),
            new CancellationTokensAccessorMock(),
            types);
    }

    [TestInitialize]
    public Task Init() {
        if (initializer != null)
            throw new Exception("неправильный жизненный цикл теста");
        initializer = new GraphStorageInitializer(Service, types);
        return initializer.InitializeAsync();
    }
}