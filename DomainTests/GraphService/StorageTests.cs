using System.Reflection;
using Abstractions;
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
    bool inited;

    public GraphServiceTests() {
        Service = new GraphService(
            Storage,
            new GraphSearchService(Storage),
            new CancellationTokensAccessorMock(),
            GraphSchemaRegistry.Create(Assemblies));
    }

    [TestInitialize]
    public Task Init() {
        if (inited)
            throw new Exception("неправильный жизненный цикл теста");
        inited = true;
        return Service.InitializeAsync();
    }
}