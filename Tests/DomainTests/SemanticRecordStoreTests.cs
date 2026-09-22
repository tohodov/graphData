using System.Text;
using System.Text.Json.Nodes;
using GraphData.Typed;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SemanticStorage;

[RelevantTestClass]
public sealed class SemanticRecordStoreTests
{
    readonly string rootPath = Path.Combine(Path.GetTempPath(), "graphdata-native-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }

    [TestMethod]
    public async Task RecordsSurviveReopen_AndReadsAreDetached()
    {
        using (var store = new SemanticRecordStore(rootPath)) {
            await store.InsertTypeAsync(Type());
            await store.InsertElementAsync(Element("item"));
            var first = (await store.ReadElementAsync("item"))!;
            ((Dictionary<string, string>)first.Attributes)["label"] = "changed snapshot";
            Assert.AreEqual("original", (await store.ReadElementAsync("item"))!.Attributes["label"]);
            await store.ReplaceAttributesAsync("item", new Dictionary<string, string> { ["label"] = "persisted" });
        }
        using var reopened = new SemanticRecordStore(rootPath);
        Assert.AreEqual("persisted", (await reopened.ReadElementAsync("item"))!.Attributes["label"]);
        Assert.AreEqual("NodeTypes/Thing", (await reopened.ReadTypesAsync()).Single().Id);
    }

    [TestMethod]
    public async Task CaseCollidingIds_AreRejectedAcrossRecordKinds_WithoutOverwriting()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("Item"));
        Assert.AreEqual(TypedGraphError.Conflict,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.InsertElementAsync(Element("item")))).Error);
        Assert.AreEqual(TypedGraphError.Conflict,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.InsertTypeAsync(Type() with { Id = "ITEM" }))).Error);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(rootPath, "instances"), "*.json").Length);
        Assert.AreEqual("Item", (await store.ReadElementAsync("Item"))!.Id);
        Assert.IsNull(await store.ReadElementAsync("item"));
        var exception = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.InsertElementAsync(Element("Item")));
        Assert.AreEqual(TypedGraphError.Conflict, exception.Error);
        Assert.AreEqual("original", (await store.ReadElementAsync("Item"))!.Attributes["label"]);
    }

    [TestMethod]
    public async Task IncidentScan_PreservesNamedParticipants_AndDeleteRestrictsReferences()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("a"));
        await store.InsertElementAsync(Element("b"));
        await store.InsertElementAsync(Relation("r2", "a", "b"));
        await store.InsertElementAsync(Relation("r1", "a", "b"));
        CollectionAssert.AreEqual(new[] { "r1", "r2" }, (await store.ReadIncidentRelationsAsync("a")).Select(record => record.Id).ToArray());
        Assert.AreEqual("b", (await store.ReadElementAsync("r1"))!.Members["target"].Single());
        var exception = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.DeleteElementAsync("a"));
        Assert.AreEqual(TypedGraphError.Conflict, exception.Error);
        await store.DeleteElementAsync("r1");
        await store.DeleteElementAsync("r2");
        await store.DeleteElementAsync("a");
        Assert.IsNull(await store.ReadElementAsync("a"));
        Assert.IsNotNull(await store.ReadElementAsync("b"));
    }

    [TestMethod]
    public void ExclusiveRootLease_IsReleasedByDispose()
    {
        using (var first = new SemanticRecordStore(rootPath)) {
            var exception = Assert.ThrowsException<TypedGraphException>(() => { using var second = new SemanticRecordStore(rootPath); });
            Assert.AreEqual(TypedGraphError.Conflict, exception.Error);
        }
        using var reopened = new SemanticRecordStore(rootPath);
    }

    [TestMethod]
    public void LegacyAndExperimentLayouts_AreRefusedWithoutWritingMarker()
    {
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "node.json"), "{}", Encoding.UTF8);
        Assert.AreEqual(TypedGraphError.Unsupported,
            Assert.ThrowsException<TypedGraphException>(() => { using var store = new SemanticRecordStore(rootPath); }).Error);
        Assert.IsFalse(File.Exists(Path.Combine(rootPath, "typed-graph-store.json")));
        Assert.IsFalse(File.Exists(Path.Combine(rootPath, ".typed-graph-store.lock")));
        File.Delete(Path.Combine(rootPath, "node.json"));
        Directory.CreateDirectory(Path.Combine(rootPath, "types"));
        Directory.CreateDirectory(Path.Combine(rootPath, "instances"));
        Assert.AreEqual(TypedGraphError.Unsupported,
            Assert.ThrowsException<TypedGraphException>(() => { using var store = new SemanticRecordStore(rootPath); }).Error);
    }

    [TestMethod]
    public async Task DuplicateRoleJson_IsCorruptRatherThanLastValueWins()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("item"));
        var path = Directory.GetFiles(Path.Combine(rootPath, "instances"), "*.json").Single();
        var json = (await File.ReadAllTextAsync(path, Encoding.UTF8)).Replace("\"members\": {}", "\"members\": {\"source\":[],\"source\":[]}", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        Assert.AreEqual(TypedGraphError.Corrupt,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.ReadElementAsync("item"))).Error);
    }

    [TestMethod]
    public async Task WrongStoredIdentity_AndUnknownVersion_AreCorrupt()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("item"));
        var path = Directory.GetFiles(Path.Combine(rootPath, "instances"), "*.json").Single();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Encoding.UTF8))!;
        json["value"]!["id"] = "another";
        await File.WriteAllTextAsync(path, json.ToJsonString(), new UTF8Encoding(false));
        Assert.AreEqual(TypedGraphError.Corrupt,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.ReadElementAsync("item"))).Error);
        json["value"]!["id"] = "item";
        json["formatVersion"] = 999;
        await File.WriteAllTextAsync(path, json.ToJsonString(), new UTF8Encoding(false));
        Assert.AreEqual(TypedGraphError.Corrupt,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.ReadIncidentRelationsAsync("item"))).Error);
    }

    [TestMethod]
    public async Task NullCollectionsAndUnknownKind_AreCorrupt()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("item"));
        var path = Directory.GetFiles(Path.Combine(rootPath, "instances"), "*.json").Single();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Encoding.UTF8))!;
        json["value"]!["members"] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString(), new UTF8Encoding(false));
        Assert.AreEqual(TypedGraphError.Corrupt,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.ReadElementAsync("item"))).Error);
        json["value"]!["members"] = new JsonObject();
        json["value"]!["kind"] = "raw-node";
        await File.WriteAllTextAsync(path, json.ToJsonString(), new UTF8Encoding(false));
        Assert.AreEqual(TypedGraphError.Corrupt,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.ReadElementAsync("item"))).Error);
    }

    [TestMethod]
    public async Task MissingParticipant_RejectedBeforeWritingRelation()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("a"));
        Assert.AreEqual(TypedGraphError.Invalid,
            (await Assert.ThrowsExceptionAsync<TypedGraphException>(() => store.InsertElementAsync(Relation("r", "a", "missing")))).Error);
        Assert.IsNull(await store.ReadElementAsync("r"));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(rootPath, "instances"), "*.json").Length);
    }

    [TestMethod]
    public async Task NamespacePrefix_DoesNotCreateOwnershipOrEdges()
    {
        using var store = new SemanticRecordStore(rootPath);
        await store.InsertElementAsync(Element("a"));
        await store.InsertElementAsync(Element("a/b"));
        await store.DeleteElementAsync("a");
        Assert.IsNotNull(await store.ReadElementAsync("a/b"));
        Assert.AreEqual(0, (await store.ReadIncidentRelationsAsync("a/b")).Count);
    }

    static TypedType Type() => new("NodeTypes/Thing", TypedElementKind.Instance, false, [], [], []);
    static TypedElement Element(string id) => new(id, TypedElementKind.Instance, ["NodeTypes/Thing"],
        new Dictionary<string, string> { ["label"] = "original" }, new Dictionary<string, IReadOnlyList<string>>());
    static TypedElement Relation(string id, string source, string target) => new(id, TypedElementKind.Relation,
        ["NodeTypes/Link"], new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>> { ["source"] = new[] { source }, ["target"] = new[] { target } });
}
