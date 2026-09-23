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

    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("semantic")]
    public async Task UntypedInstances_SupportExistingEditorCreation(string mode) {
        await using var scope = new Scope(mode);
        var graph = await scope.Open();
        await graph.CreateInstanceAsync("note", [], new Dictionary<string, string> { ["text"] = "draft" });
        graph = await scope.Open();
        var note = await graph.GetAsync("note");
        Assert.AreEqual(0, note.TypeIds.Count);
        Assert.AreEqual("draft", note.Attributes["text"]);
        await graph.DeleteAsync("note");
    }

    [TestMethod]
    public async Task NativeRelationEditing_PreservesFactIdentityEvidenceAndReopen() {
        await using var scope = new Scope("semantic");
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Item"));
        await graph.CreateTypeAsync(RelationType());
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Evidence", TypedElementKind.Relation, false, [],
            [new("Fact", "NodeTypes/Contains"), new("Source", "NodeTypes/Item")], []));
        foreach (var id in new[] { "a", "b", "c" })
            await graph.CreateInstanceAsync(id, ["NodeTypes/Item"], new Dictionary<string, string>());
        await graph.CreateRelationAsync("fact", "NodeTypes/Contains", Members("a", "b"), new Dictionary<string, string> { ["name"] = "the same fact" });
        await graph.CreateRelationAsync("evidence", "NodeTypes/Evidence", new Dictionary<string, IReadOnlyList<string>> { ["Fact"] = ["fact"], ["Source"] = ["a"] }, new Dictionary<string, string>());

        await AssertError(() => graph.ReplaceMembersAsync("fact", Members("a", "missing")), TypedGraphError.Invalid);
        Assert.AreEqual("b", (await graph.GetAsync("fact")).Members["Items"].Single());
        await graph.ReplaceMembersAsync("fact", Members("a", "c"));
        Assert.AreEqual(0, (await graph.FindIncidentRelationsAsync("b")).Count);
        Assert.AreEqual("fact", (await graph.FindIncidentRelationsAsync("c")).Single().Id);
        await AssertError(() => graph.DeleteAsync("fact"), TypedGraphError.Conflict);

        graph = await scope.Open();
        var reopened = await graph.GetAsync("fact");
        Assert.AreEqual("c", reopened.Members["Items"].Single());
        Assert.AreEqual("the same fact", reopened.Attributes["name"]);
        Assert.AreEqual("fact", (await graph.GetAsync("evidence")).Members["Fact"].Single());
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "evidence", "fact" }, (await graph.GetElementsAsync()).Select(element => element.Id).ToArray());
    }

    [TestMethod]
    public async Task NativeTypeEditing_ValidatesExistingValuesAndRestrictsIncomingRequirements() {
        await using var scope = new Scope("semantic");
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Entity", isAbstract: true));
        await graph.CreateTypeAsync(InstanceType("Item", required: ["NodeTypes/Entity"]) with { Attributes = [new("Count", ScalarKind.Int32, true)] });
        await graph.CreateTypeAsync(RelationType());
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Evidence", TypedElementKind.Relation, false, [],
            [new("Fact", "NodeTypes/Contains"), new("Source", null)], []));
        await graph.CreateInstanceAsync("a", [], new Dictionary<string, string> { ["Count"] = "invalid" });
        await AssertError(() => graph.ReplaceTypesAsync("a", ["NodeTypes/Item"]), TypedGraphError.Invalid);
        Assert.AreEqual(0, (await graph.GetAsync("a")).TypeIds.Count);
        await graph.ReplaceAttributesAsync("a", new Dictionary<string, string> { ["Count"] = "2" });
        var typed = await graph.ReplaceTypesAsync("a", ["NodeTypes/Item"]);
        CollectionAssert.AreEqual(new[] { "NodeTypes/Entity", "NodeTypes/Item" }, typed.TypeIds.ToArray());
        await graph.CreateInstanceAsync("b", ["NodeTypes/Item"], new Dictionary<string, string> { ["Count"] = "1" });
        await graph.CreateRelationAsync("fact", "NodeTypes/Contains", Members("a", "b"), new Dictionary<string, string>());
        await graph.CreateRelationAsync("evidence", "NodeTypes/Evidence", new Dictionary<string, IReadOnlyList<string>> { ["Fact"] = ["fact"], ["Source"] = ["a"] }, new Dictionary<string, string>());
        await AssertError(() => graph.ReplaceTypesAsync("a", []), TypedGraphError.Conflict);
        await AssertError(() => graph.ReplaceTypesAsync("fact", []), TypedGraphError.Conflict);
        CollectionAssert.AreEqual(new[] { "NodeTypes/Contains" }, (await graph.GetAsync("fact")).TypeIds.ToArray());
        await graph.DeleteAsync("evidence");
        await graph.ReplaceTypesAsync("fact", []);
        await graph.ReplaceTypesAsync("a", []);
        graph = await scope.Open();
        Assert.AreEqual(0, (await graph.GetAsync("a")).TypeIds.Count);
        Assert.AreEqual(0, (await graph.GetAsync("fact")).TypeIds.Count);
        Assert.AreEqual("b", (await graph.GetAsync("fact")).Members["Items"].Single());
    }

    [TestMethod]
    public async Task NativeUntypedRelations_ValidateParticipantsSchemasAndCyclesBeforeMutation() {
        await using var scope = new Scope("semantic");
        var graph = await scope.Open();
        await graph.CreateInstanceAsync("a", [], new Dictionary<string, string>());
        await graph.CreateInstanceAsync("b", [], new Dictionary<string, string>());
        await graph.CreateRelationAsync("fact", null, Members("a", "b"), new Dictionary<string, string>());
        await graph.CreateRelationAsync("evidence", null, Members("fact", "b"), new Dictionary<string, string>());
        await AssertError(() => graph.ReplaceMembersAsync("fact", Members("evidence", "b")), TypedGraphError.Invalid);
        await AssertError(() => graph.ReplaceMembersAsync("fact", Members("fact", "b")), TypedGraphError.Invalid);
        await AssertError(() => graph.ReplaceMembersAsync("fact", new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = ["a"] }), TypedGraphError.Invalid);
        await AssertError(() => graph.ReplaceMembersAsync("fact", new Dictionary<string, IReadOnlyList<string>> { ["Owner"] = ["a"], ["Items"] = ["b", "b"] }), TypedGraphError.Invalid);
        await AssertError(() => graph.ReplaceMembersAsync("fact", Members("a", "missing")), TypedGraphError.Invalid);
        Assert.AreEqual("a", (await graph.GetAsync("fact")).Members["Owner"].Single());

        await graph.CreateTypeAsync(new TypedType("NodeTypes/Link", TypedElementKind.Relation, false, [], [new("Source", null), new("Target", null)], []));
        await AssertError(() => graph.ReplaceTypesAsync("fact", ["NodeTypes/Link"]), TypedGraphError.Invalid);
        Assert.AreEqual(0, (await graph.GetAsync("fact")).TypeIds.Count);
        await graph.ReplaceMembersAsync("fact", new Dictionary<string, IReadOnlyList<string>> { ["Source"] = ["a"], ["Target"] = ["b"] });
        await graph.ReplaceTypesAsync("fact", ["NodeTypes/Link"]);
        Assert.AreEqual("NodeTypes/Link", (await graph.GetAsync("fact")).TypeIds.Single());
        await graph.ReplaceTypesAsync("fact", []);
        Assert.AreEqual(0, (await graph.GetAsync("fact")).TypeIds.Count);
    }

    [TestMethod]
    public async Task NativeRelationReplacement_ChangesSchemaAtomicallyAndPreservesEvidence() {
        await using var scope = new Scope("semantic");
        var graph = await scope.Open();
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Link", TypedElementKind.Relation, false, [], [new("Source", null), new("Target", null)], []));
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Flight", TypedElementKind.Relation, false, [], [new("Origin", null), new("Destination", null)], [new("Distance", ScalarKind.Int32, true)]));
        await graph.CreateTypeAsync(new TypedType("NodeTypes/Evidence", TypedElementKind.Relation, false, [], [new("Fact", "NodeTypes/Link"), new("Source", null)], []));
        foreach (var id in new[] { "a", "b", "c" })
            await graph.CreateInstanceAsync(id, [], new Dictionary<string, string>());
        await graph.CreateRelationAsync("fact", "NodeTypes/Link", new Dictionary<string, IReadOnlyList<string>> { ["Source"] = ["a"], ["Target"] = ["b"] }, new Dictionary<string, string> { ["name"] = "original" });
        await graph.CreateRelationAsync("evidence", null, Members("fact", "a"), new Dictionary<string, string>());
        await graph.CreateRelationAsync("typed-evidence", "NodeTypes/Evidence", new Dictionary<string, IReadOnlyList<string>> { ["Fact"] = ["fact"], ["Source"] = ["a"] }, new Dictionary<string, string>());
        var changedMembers = new Dictionary<string, IReadOnlyList<string>> { ["Origin"] = ["a"], ["Destination"] = ["c"] };
        var changedAttributes = new Dictionary<string, string> { ["Distance"] = "100" };

        await AssertError(() => graph.ReplaceRelationAsync("fact", "NodeTypes/Flight", changedMembers, changedAttributes), TypedGraphError.Conflict);
        var original = await graph.GetAsync("fact");
        Assert.AreEqual("NodeTypes/Link", original.TypeIds.Single());
        Assert.AreEqual("b", original.Members["Target"].Single());
        Assert.AreEqual("original", original.Attributes["name"]);

        await graph.DeleteAsync("typed-evidence");
        await AssertError(() => graph.ReplaceRelationAsync("fact", "NodeTypes/Flight", changedMembers, new Dictionary<string, string> { ["Distance"] = "invalid" }), TypedGraphError.Invalid);
        Assert.AreEqual("NodeTypes/Link", (await graph.GetAsync("fact")).TypeIds.Single());
        await graph.ReplaceRelationAsync("fact", "NodeTypes/Flight", changedMembers, changedAttributes);
        graph = await scope.Open();
        var updated = await graph.GetAsync("fact");
        Assert.AreEqual("fact", updated.Id);
        Assert.AreEqual("NodeTypes/Flight", updated.TypeIds.Single());
        Assert.AreEqual("c", updated.Members["Destination"].Single());
        Assert.AreEqual("100", updated.Attributes["Distance"]);
        Assert.IsFalse(updated.Attributes.ContainsKey("name"));
        Assert.AreEqual("fact", (await graph.GetAsync("evidence")).Members["Owner"].Single());
        await AssertError(() => graph.DeleteAsync("fact"), TypedGraphError.Conflict);
    }

    [TestMethod]
    public async Task NativeList_ReturnsDetachedSnapshots() {
        await using var scope = new Scope("semantic");
        var graph = await scope.Open();
        await graph.CreateTypeAsync(InstanceType("Item"));
        await graph.CreateInstanceAsync("a", ["NodeTypes/Item"], new Dictionary<string, string> { ["name"] = "original" });
        await graph.CreateInstanceAsync("b", [], new Dictionary<string, string>());
        await graph.CreateRelationAsync("fact", null, Members("a", "b"), new Dictionary<string, string>());
        var elements = await graph.GetElementsAsync();
        ((string[])elements.Single(element => element.Id == "a").TypeIds)[0] = "NodeTypes/Changed";
        ((string[])elements.Single(element => element.Id == "fact").Members["Items"])[0] = "a";
        Assert.AreEqual("NodeTypes/Item", (await graph.GetAsync("a")).TypeIds.Single());
        Assert.AreEqual("b", (await graph.GetAsync("fact")).Members["Items"].Single());
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
                var graph = await Graph.OpenAsync(storage, GraphSchemaRegistry.Create(Array.Empty<System.Reflection.Assembly>()));
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
