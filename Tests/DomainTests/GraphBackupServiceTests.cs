using Abstractions;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

            var backup = new GraphBackupService(Service);
            await backup.SaveAsync(backupPath);
            Assert.IsTrue(File.Exists(backupPath));

            Directory.Delete(StorageOptions.RootPath, recursive: true);

            await backup.RestoreAsync(backupPath);

            var restoredEmptyChild = await Service.GetNode(new NodePath("empty-parent", "empty-child"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredEmptyChild.Status, restoredEmptyChild.Error);
            Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "empty-parent", "empty-child")));
            Assert.IsFalse(File.Exists(Path.Combine(StorageOptions.RootPath, "empty-parent", "empty-child", StorageOptions.MetadataFileName)));

            var restoredSource = await Service.GetNode(new NodePath("source"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredSource.Status, restoredSource.Error);
            Assert.AreEqual("Source node", restoredSource.Value!.Attributes["label"]);

            var restoredTarget = await Service.GetNode(new NodePath("target"));
            Assert.AreEqual(ServiceResultStatus.Ok, restoredTarget.Status, restoredTarget.Error);

            var storedSource = await Storage.Get(new NodePath("source"));
            Assert.IsNotNull(storedSource);
            Assert.IsTrue(await storedSource.Nodes.AnyAsync(node => node.GlobalId == restoredTarget.Value!.GlobalId));
            Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "source", "target")).HasFlag(FileAttributes.ReparsePoint));
            Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "target", "source")).HasFlag(FileAttributes.ReparsePoint));

            Assert.AreEqual(emptyChild.LocalId, restoredEmptyChild.Value!.LocalId);
        } finally {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }
}
