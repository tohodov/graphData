using System.IO;
using System.Threading.Tasks;
using Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class SymLinkGraphStorageTests : GraphStorageContractTests
{
    [TestMethod]
    public async Task CreateNodeAsync_ShouldCreateDirectoryAndMetadataFile()
    {
        var node = await CreateNode();
        var nodeDirectory = Path.Combine(StorageOptions.RootPath, node.LocalId);
        Assert.IsTrue(Directory.Exists(nodeDirectory));

        var metadataPath = Path.Combine(nodeDirectory, StorageOptions.MetadataFileName);
        Assert.IsTrue(File.Exists(metadataPath));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldCreateDirectoryLinks()
    {
        var first = await CreateNode();
        var second = await CreateNode();

        await first.Nodes.Add(second);

        var firstLink = Path.Combine(StorageOptions.RootPath, first.LocalId, second.LocalId);
        var secondLink = Path.Combine(StorageOptions.RootPath, second.LocalId, first.LocalId);

        Assert.IsTrue(Directory.Exists(firstLink) || File.Exists(firstLink));
        Assert.IsTrue(Directory.Exists(secondLink) || File.Exists(secondLink));

        var expectedFirstTarget = Path.Combine(StorageOptions.RootPath, second.LocalId);
        var expectedSecondTarget = Path.Combine(StorageOptions.RootPath, first.LocalId);

        Assert.AreEqual(Path.GetFullPath(expectedFirstTarget), new DirectoryInfo(firstLink).LinkTarget);
        Assert.AreEqual(Path.GetFullPath(expectedSecondTarget), new DirectoryInfo(secondLink).LinkTarget);
    }

    [TestMethod]
    public async Task NodeNeighborsAsync_ShouldReflectFolderChangesAfterFirstRead()
    {
        var parent = await Storage.Create(new("parent"));
        var first = await Storage.Create(new("first"), parent.GlobalId);
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            await ReadNeighborLocalIdsAsync(parent));

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            await ReadNeighborLocalIdsAsync(parent));

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            await ReadNeighborLocalIdsAsync(parent));
    }

    private static async Task<NodeLocalId[]> ReadNeighborLocalIdsAsync(NodeBacking node)
    {
        var result = new List<NodeLocalId>();
        await foreach (var neighbor in node.Nodes)
            result.Add(neighbor.LocalId);
        return result.ToArray();
    }
}
