using GraphData.Core.Services;
using GraphData.Typed;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SemanticStorage;
using Storage;

[RelevantTestClass]
public sealed class TypedGraphContractTests
{
    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("semantic")]
    public async Task SameContract_RolesParallelRelationsReopenAndRestrictDelete(string mode) {
        await using var scope = new Scope(mode);
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Entity", isAbstract: true));
        await graph.CreateTypeAsync(InstanceType("Item", required: ["NodeTypes/Entity"]));
        await graph.CreateTypeAsync(RelationType());
        var a = await graph.CreateInstanceAsync("a", ["NodeTypes/Item"], new Dictionary<string, string> { ["name"] = "A" });
        var b = await graph.CreateInstanceAsync("b", ["NodeTypes/Item"], new Dictionary<string, string>());
        CollectionAssert.AreEquivalent(new[] { "NodeTypes/Entity", "NodeTypes/Item" }, a.TypeIds.ToArray());
        var members = Members("a", "b");
        await graph.CreateRelationAsync("r1", "NodeTypes/Contains", members, new Dictionary<string, string>());
        await graph.CreateRelationAsync("r2", "NodeTypes/Contains", members, new Dictionary<string, string>());
        await graph.ReplaceAttributesAsync("a", new Dictionary<string, string> { ["name"] = "renamed" });
        await AssertError(() => graph.DeleteAsync("a"), TypedGraphError.Conflict);
        graph = await scope.Open();
        Assert.AreEqual("renamed", (await graph.GetAsync("a")).Attributes["name"]);
        CollectionAssert.AreEqual(new[] { "r1", "r2" }, (await graph.FindIncidentRelationsAsync("a")).Select(item => item.Id).ToArray());
        var relation = await graph.GetAsync("r1");
        CollectionAssert.AreEqual(new[] { "a" }, relation.Members["Owner"].ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, relation.Members["Items"].ToArray());
        await graph.DeleteAsync("r1");
        await graph.DeleteAsync("r2");
        await graph.DeleteAsync("a");
        await AssertError(() => graph.GetAsync("a"), TypedGraphError.NotFound);
        Assert.AreEqual("b", (await graph.GetAsync("b")).Id);
    }

    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("semantic")]
    public async Task InvalidWrites_DoNotCreateOrOverwriteData(string mode) {
        await using var scope = new Scope(mode);
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Item"));
        await graph.CreateTypeAsync(RelationType());
        await graph.CreateInstanceAsync("a", ["NodeTypes/Item"], new Dictionary<string, string> { ["name"] = "original" });
        await AssertError(() => graph.CreateInstanceAsync("a", ["NodeTypes/Item"], new Dictionary<string, string> { ["name"] = "overwritten" }), TypedGraphError.Conflict);
        Assert.AreEqual("original", (await graph.GetAsync("a")).Attributes["name"]);
        await AssertError(() => graph.CreateRelationAsync("bad", "NodeTypes/Contains", Members("a", "missing"), new Dictionary<string, string>()), TypedGraphError.Invalid);
        await AssertError(() => graph.GetAsync("bad"), TypedGraphError.NotFound);
        await AssertError(() => graph.CreateRelationAsync("bad", "NodeTypes/Contains", new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = ["a"], ["Items"] = ["a", "a"] }, new Dictionary<string, string>()), TypedGraphError.Invalid);
        await AssertError(() => graph.CreateRelationAsync("bad", "NodeTypes/Contains", new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = ["a"], ["Items"] = [], ["Unexpected"] = ["a"] }, new Dictionary<string, string>()), TypedGraphError.Invalid);
        Assert.AreEqual(0, (await graph.FindIncidentRelationsAsync("a")).Count);
    }

    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("semantic")]
    public async Task ScalarValidationAndCollections_AreShared(string mode) {
        await using var scope = new Scope(mode);
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Item") with { Attributes = [new("Count", ScalarKind.Int32, true)] });
        await graph.CreateTypeAsync(RelationType());
        await AssertError(() => graph.CreateInstanceAsync("bad", ["NodeTypes/Item"], new Dictionary<string, string> { ["Count"] = "NaN" }), TypedGraphError.Invalid);
        foreach (var id in new[] { "a", "b", "c" })
            await graph.CreateInstanceAsync(id, ["NodeTypes/Item"], new Dictionary<string, string> { ["Count"] = "1" });
        await graph.CreateRelationAsync("many", "NodeTypes/Contains", new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = ["a"], ["Items"] = ["c", "b"] }, new Dictionary<string, string>());
        CollectionAssert.AreEqual(new[] { "b", "c" }, (await graph.GetAsync("many")).Members["Items"].ToArray());
        await AssertError(() => graph.ReplaceAttributesAsync("a", new Dictionary<string, string>()), TypedGraphError.Invalid);
        Assert.AreEqual("1", (await graph.GetAsync("a")).Attributes["Count"]);
    }

    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("semantic")]
    public async Task HigherOrderRelations_UseRelationIdentityAsParticipant(string mode) {
        await using var scope = new Scope(mode);
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Item"));
        await graph.CreateTypeAsync(RelationType());
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Evidence", TypedElementKind.Relation, false, [], [new("Fact", "NodeTypes/Contains"), new("Source", "NodeTypes/Item")], []));
        await graph.CreateInstanceAsync("a", ["NodeTypes/Item"], new Dictionary<string, string>());
        await graph.CreateInstanceAsync("b", ["NodeTypes/Item"], new Dictionary<string, string>());
        await graph.CreateRelationAsync("fact", "NodeTypes/Contains", Members("a", "b"), new Dictionary<string, string>());
        await graph.CreateRelationAsync("evidence", "NodeTypes/Evidence", new Dictionary<string, IReadOnlyList<string>> { ["Fact"] = ["fact"], ["Source"] = ["a"] }, new Dictionary<string, string>());
        await AssertError(() => graph.DeleteAsync("fact"), TypedGraphError.Conflict);
        Assert.AreEqual("evidence", (await graph.FindIncidentRelationsAsync("fact")).Single().Id);
    }

    static TypedType InstanceType(string name, bool isAbstract = false, IReadOnlyList<string>? required = null) => new($"NodeTypes/{name}", TypedElementKind.Instance, isAbstract, required ?? [], [], []);
    static TypedType RelationType() => new("NodeTypes/Contains", TypedElementKind.Relation, false, [], [new("Owner", "NodeTypes/Item"), new("Items", "NodeTypes/Item", 0, null)], []);
    static IReadOnlyDictionary<string, IReadOnlyList<string>> Members(string owner, string item) => new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = [owner], ["Items"] = [item] };
    static async Task AssertError(Func<Task> action, TypedGraphError error) {
        var exception = await Assert.ThrowsExceptionAsync<TypedGraphException>(action);
        Assert.AreEqual(error, exception.Error, exception.Message);
    }

    sealed class Scope(string mode) : IAsyncDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "GraphDataTypedContract", Guid.NewGuid().ToString("N"));
        TypedGraphService? service;
        ITypedGraphStore? store;

        public async Task<ITypedGraph> Open() {
            service?.Dispose();
            (store as IDisposable)?.Dispose();
            if (mode == "semantic") {
                store = new SemanticRecordStore(root);
            } else {
                var storage = new SymLinkGraphStorage(Options.Create(new NtfsGraphStorageOptions { RootPath = root }), new CancellationTokensAccessorMock());
                var graph = await CarrierGraph.OpenAsync(storage, GraphSchemaRegistry.Create(Array.Empty<System.Reflection.Assembly>()));
                store = new CarrierTypedGraphStore(graph);
            }
            service = new TypedGraphService(store);
            return service;
        }

        public ValueTask DisposeAsync() {
            service?.Dispose();
            (store as IDisposable)?.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }
}
