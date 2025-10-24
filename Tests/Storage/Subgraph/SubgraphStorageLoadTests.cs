using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.SubgraphStorage;
using GraphData.SubgraphStorage.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage.Subgraph;

[TestClass]
public sealed class SubgraphStorageLoadTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    public async Task RandomAccessStorage_ShouldHandleDenseSubgraphQueries()
    {
        var randomRoot = CreateTempRoot("random");
        var bucketRoot = CreateTempRoot("bucket");

        var randomStorage = new RandomAccessGraphStorage(Options.Create(new RandomAccessGraphStorageOptions
        {
            RootPath = randomRoot
        }), NullLogger<RandomAccessGraphStorage>.Instance);

        var bucketStorage = new LargeSubgraphGraphStorage(Options.Create(new LargeSubgraphGraphStorageOptions
        {
            RootPath = bucketRoot,
            BucketPrefixLength = 2
        }), NullLogger<LargeSubgraphGraphStorage>.Instance);

        try
        {
            var nodes = CreateGraphMetadata(400);
            await PopulateStorageAsync(randomStorage, nodes, fanOut: 6).ConfigureAwait(false);
            await PopulateStorageAsync(bucketStorage, nodes, fanOut: 6).ConfigureAwait(false);

            var seed = nodes[0].Id;
            var query = new SubgraphQuery
            {
                RootNodeIds = new[] { seed },
                MaxDepth = 3
            };

            var bucketWatch = Stopwatch.StartNew();
            var bucketResult = await bucketStorage.GetSubgraphAsync(query).ConfigureAwait(false);
            bucketWatch.Stop();

            var randomWatch = Stopwatch.StartNew();
            var randomResult = await randomStorage.GetSubgraphAsync(query).ConfigureAwait(false);
            randomWatch.Stop();

            Assert.AreEqual(bucketResult.Nodes.Count, randomResult.Nodes.Count, "Both storages should return the same node count.");
            CollectionAssert.AreEquivalent(bucketResult.Nodes.Keys.ToList(), randomResult.Nodes.Keys.ToList());

            TestContext.WriteLine($"Bucketed storage: {bucketWatch.ElapsedMilliseconds} ms");
            TestContext.WriteLine($"Random access storage: {randomWatch.ElapsedMilliseconds} ms");

            Assert.IsTrue(randomWatch.Elapsed <= TimeSpan.FromMilliseconds(bucketWatch.ElapsedMilliseconds * 2 + 50), "Random access storage should not be significantly slower than bucketed storage.");
        }
        finally
        {
            CleanupRoot(randomRoot);
            CleanupRoot(bucketRoot);
        }
    }

    private static async Task PopulateStorageAsync(IGraphStorage storage, IList<NodeMetadata> nodes, int fanOut)
    {
        foreach (var node in nodes)
        {
            await storage.CreateNodeAsync(node).ConfigureAwait(false);
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            var source = nodes[index];
            for (var offset = 1; offset <= fanOut; offset++)
            {
                var targetIndex = (index + offset) % nodes.Count;
                var target = nodes[targetIndex];
                await storage.ConnectNodesAsync(source.Id, target.Id).ConfigureAwait(false);
            }
        }
    }

    private static List<NodeMetadata> CreateGraphMetadata(int count)
    {
        var list = new List<NodeMetadata>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new NodeMetadata
            {
                Id = Guid.NewGuid(),
                Name = $"Node-{i}",
                Attributes = new Dictionary<string, string>
                {
                    ["index"] = i.ToString(),
                    ["category"] = (i % 10).ToString()
                }
            });
        }

        return list;
    }

    private static string CreateTempRoot(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "GraphDataTests", prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
