using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.PerNodeFileStorage;
using GraphData.PerNodeFileStorage.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage.Files;

[TestClass]
public sealed class PerNodeFileGraphStorageTests : GraphStorageContractTests
{
    private PerNodeFileGraphStorageOptions options = null!;

    protected override Task<IGraphStorage> CreateStorageAsync()
    {
        options = new PerNodeFileGraphStorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", Guid.NewGuid().ToString("N"))
        };

        IGraphStorage storage = new PerNodeFileGraphStorage(Options.Create(options), NullLogger<PerNodeFileGraphStorage>.Instance);
        return Task.FromResult(storage);
    }

    protected override async Task OnCleanupAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(options?.RootPath) && Directory.Exists(options.RootPath))
            {
                Directory.Delete(options.RootPath, recursive: true);
            }
        }
        finally
        {
            options = null!;
            await base.OnCleanupAsync();
        }
    }

    [TestMethod]
    public async Task Create_ShouldStoreNodeAsDedicatedMetadataFile()
    {
        await CreateNode();

        var metadataRoot = Path.Combine(options.RootPath, options.MetadataDirectoryName);
        Assert.IsTrue(Directory.EnumerateFiles(metadataRoot, $"*{options.MetadataFileExtension}").Any());
    }
}
