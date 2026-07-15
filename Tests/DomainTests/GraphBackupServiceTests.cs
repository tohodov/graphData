using Abstractions;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using System.Text.Json.Nodes;

[RelevantTestClass]
public sealed class GraphBackupServiceTests : GraphServiceTests {
    [TestMethod]
    public async Task RestoreAsync_ShouldRecreateWholeGraphAfterStorageDirectoryWasDeleted() {
        var backupPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", $"{Guid.NewGuid():N}.graph-backup.json");
        try {
            var emptyParent = (await Service.CreateNode(new NodeLocalId("empty-parent"))).Value!;
            var emptyChild = (await Service.CreateNode(new NodeLocalId("empty-child"), emptyParent.GlobalId)).Value!;
            var source = (await Service.CreateNode(
                new NodeLocalId("source"),
                attributes: new Dictionary<string, string> {
                    ["label"] = "Source node"
                })).Value!;
            var target = (await Service.CreateNode(new NodeLocalId("target"))).Value!;
            var connect = await Service.ConnectNodesAsync(source.GlobalId, target.GlobalId);
            Assert.AreEqual(ServiceResultStatus.Ok, connect.Status, connect.Error);
            var semanticStorage = (ISemanticGraphStorage)Storage;
            var sourceRelation = (await semanticStorage
                .FindIncidentRelationsAsync(source.GlobalId)
                .ToArrayAsync()).Single();

            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);
            Assert.IsTrue(File.Exists(backupPath));

            Directory.Delete(StorageOptions.RootPath, recursive: true);

            await backup.RestoreAsync(backupPath);

            var restoredEmptyChild = await Service.GetNode(new NodePath("empty-parent", "empty-child"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredEmptyChild.Status, restoredEmptyChild.Error);
            Assert.IsTrue(File.Exists(Path.Combine(
                StorageOptions.RootPath,
                StorageOptions.InstancesDirectoryName,
                "empty-parent",
                "empty-child",
                "instance.json")));

            var restoredSource = await Service.GetNode(new NodePath("source"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredSource.Status, restoredSource.Error);
            Assert.AreEqual("Source node", restoredSource.Value!.Attributes["label"]);

            var restoredTarget = await Service.GetNode(new NodePath("target"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredTarget.Status, restoredTarget.Error);
            var restoredTargetNode = restoredTarget.Value!;

            var storedSource = await Storage.Get(new NodePath("source"));
            Assert.IsNotNull(storedSource);
            Assert.IsTrue(await storedSource.Nodes.AnyAsync(node => node.GlobalId == restoredTargetNode.GlobalId));
            var restoredRelations = await semanticStorage
                .FindIncidentRelationsAsync(storedSource.GlobalId)
                .ToArrayAsync();
            Assert.AreEqual(1, restoredRelations.Length);
            Assert.AreEqual(sourceRelation.Id, restoredRelations.Single().Id);
            CollectionAssert.AreEquivalent(
                new[] { storedSource.GlobalId, restoredTargetNode.GlobalId },
                restoredRelations.Single().Members
                    .SelectMany(static member => member.ParticipantIds)
                    .ToArray());
            Assert.IsTrue(File.Exists(Path.Combine(
                StorageOptions.RootPath,
                StorageOptions.InstancesDirectoryName,
                sourceRelation.Id.ToString().Replace('/', Path.DirectorySeparatorChar),
                "instance.json")));
            Assert.IsTrue(File.Exists(Path.Combine(
                StorageOptions.RootPath,
                StorageOptions.IndexesDirectoryName,
                "incidents",
                source.LocalId,
                "relations.json")));

            Assert.AreEqual(emptyChild.LocalId, restoredEmptyChild.Value!.LocalId);
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    [TestMethod]
    public async Task RestoreAsync_ShouldValidateEveryRelationBeforeMutatingExistingElements() {
        var backupPath = TemporaryBackupPath();
        try {
            var source = (await Service.CreateNode(
                new NodeLocalId("source"),
                attributes: new Dictionary<string, string> { ["label"] = "before" })).Value!;
            var target = (await Service.CreateNode(new NodeLocalId("target"))).Value!;
            var connect = await Service.ConnectNodesAsync(source.GlobalId, target.GlobalId);
            Assert.AreEqual(ServiceResultStatus.Ok, connect.Status, connect.Error);

            var semanticStorage = (ISemanticGraphStorage)Storage;
            var originalRelation = (await semanticStorage
                .FindIncidentRelationsAsync(source.GlobalId)
                .ToArrayAsync()).Single();
            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);

            var document = await ReadBackupAsync(backupPath);
            var sourceRecord = FindRecord(document, "source");
            sourceRecord["attributes"]!["label"] = "after";
            var relationRecord = FindRecord(document, originalRelation.Id.ToString());
            var members = relationRecord["members"]!.AsArray();
            members.Add(members[0]!.DeepClone());
            await WriteBackupAsync(backupPath, document);

            await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => backup.RestoreAsync(backupPath));

            var storedSource = await Storage.Get(source.GlobalId);
            Assert.IsNotNull(storedSource);
            Assert.AreEqual("before", storedSource.Attributes["label"]);
            var storedRelation = await semanticStorage.GetRelationAsync(originalRelation.Id);
            Assert.IsNotNull(storedRelation);
            Assert.AreEqual(originalRelation.Members.Count, storedRelation.Members.Count);
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    [TestMethod]
    public async Task RestoreAsync_ShouldRestoreIndependentInstanceBelowMissingNamespaceRecord() {
        var backupPath = TemporaryBackupPath();
        try {
            var parent = (await Service.CreateNode(new NodeLocalId("namespace"))).Value!;
            var child = (await Service.CreateNode(new NodeLocalId("independent"), parent.GlobalId)).Value!;
            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);

            var document = await ReadBackupAsync(backupPath);
            var records = document["records"]!.AsArray();
            Assert.IsTrue(records.Remove(FindRecord(document, "namespace")));
            await WriteBackupAsync(backupPath, document);

            Directory.Delete(StorageOptions.RootPath, recursive: true);
            await backup.RestoreAsync(backupPath);

            Assert.IsNull(await Storage.Get(parent.GlobalId));
            var restoredChild = await Storage.Get(child.GlobalId);
            Assert.IsNotNull(restoredChild);
            Assert.AreEqual(child.GlobalId, restoredChild.GlobalId);

            var frontier = await ((ISemanticGraphStorage)Storage)
                .EnumerateInstanceNamespaceChildrenAsync()
                .ToArrayAsync();
            Assert.IsTrue(
                frontier.Any(node => node.GlobalId == child.GlobalId),
                "A real instance below a transparent namespace must remain discoverable from the root frontier.");
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    [TestMethod]
    public async Task RestoreAsync_ShouldReplaceRelationWithoutDeletingHigherOrderDependents() {
        var backupPath = TemporaryBackupPath();
        try {
            var left = (await Service.CreateNode(new NodeLocalId("left"))).Value!;
            var oldRight = (await Service.CreateNode(new NodeLocalId("old-right"))).Value!;
            var newRight = (await Service.CreateNode(new NodeLocalId("new-right"))).Value!;
            var semantic = (ISemanticGraphStorage)Storage;
            var relation = await semantic.CreateRelationAsync(
                new NodeLocalId("base-relation"),
                Array.Empty<NodeRef>(),
                [
                    new SemanticRelationMember("Left", new InternalId(), [left.GlobalId]),
                    new SemanticRelationMember("Right", new InternalId(), [oldRight.GlobalId])
                ]);
            var annotation = await semantic.CreateRelationAsync(
                new NodeLocalId("annotation"),
                Array.Empty<NodeRef>(),
                [new SemanticRelationMember("Subject", new InternalId(), [relation.GlobalId])]);

            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);
            var document = await ReadBackupAsync(backupPath);
            var relationRecord = FindRecord(document, relation.GlobalId.ToString());
            var rightMember = relationRecord["members"]!.AsArray()
                .Select(static node => node!.AsObject())
                .Single(static member => member["name"]!.GetValue<string>() == "Right");
            var participants = rightMember["participantIds"]!.AsArray();
            participants.Clear();
            participants.Add(new JsonArray(newRight.GlobalId
                .Select(static segment => (JsonNode?)JsonValue.Create(segment.ToString()))
                .ToArray()));
            await WriteBackupAsync(backupPath, document);

            await backup.RestoreAsync(backupPath);

            var restoredRelation = await semantic.GetRelationAsync(relation.GlobalId);
            Assert.IsNotNull(restoredRelation);
            CollectionAssert.AreEqual(
                new[] { newRight.GlobalId },
                restoredRelation.Members.Single(static member => member.Name == "Right").ParticipantIds.ToArray());
            var restoredAnnotation = await semantic.GetRelationAsync(annotation.GlobalId);
            Assert.IsNotNull(restoredAnnotation, "Replacing a relation must preserve relations that refer to its stable id.");
            CollectionAssert.AreEqual(
                new[] { relation.GlobalId },
                restoredAnnotation.Members.Single().ParticipantIds.ToArray());
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    [TestMethod]
    public async Task RestoreAsync_ShouldRejectPhysicalCaseCollisionBeforeAnyMutation() {
        if (!OperatingSystem.IsWindows())
            return;

        var backupPath = TemporaryBackupPath();
        try {
            var first = (await Service.CreateNode(
                "aaa",
                attributes: new Dictionary<string, string> { ["state"] = "before" })).Value!;
            _ = (await Service.CreateNode("CaseNode")).Value!;
            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);

            var document = await ReadBackupAsync(backupPath);
            FindRecord(document, first.GlobalId.ToString())["attributes"]!["state"] = "after";
            FindRecord(document, "CaseNode")["path"] = new JsonArray("casenode");
            await WriteBackupAsync(backupPath, document);

            var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => backup.RestoreAsync(backupPath));

            StringAssert.Contains(error.Message, "physically collides");
            Assert.AreEqual("before", (await Storage.Get(first.GlobalId))!.Attributes["state"]);
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    private static string TemporaryBackupPath() =>
        Path.Combine(Path.GetTempPath(), "GraphDataTests", $"{Guid.NewGuid():N}.graph-backup.json");

    private static async Task<JsonObject> ReadBackupAsync(string path) =>
        (JsonNode.Parse(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("Test backup is empty.")).AsObject();

    private static async Task WriteBackupAsync(string path, JsonObject document) =>
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static JsonObject FindRecord(JsonObject document, string path)
    {
        return document["records"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(record => string.Equals(
                string.Join('/', record["path"]!.AsArray().Select(static segment => segment!.GetValue<string>())),
                path,
                StringComparison.Ordinal));
    }
}
