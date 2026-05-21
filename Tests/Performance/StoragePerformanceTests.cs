using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Performance;

[TestClass]
[TestCategory("Performance")]
public sealed class StoragePerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow("PerNodeFile", 250, 2, 1729)]
    [DataRow("BucketedFile", 250, 2, 1729)]
    [DataRow("SymLink", 250, 2, 1729)]
    public async Task StorageOperations_ShouldRecordDetailedTimings(
        string storageKindName,
        int nodeCount,
        int connectionsPerNode,
        int seed)
    {
        var storageKind = ParseStorageKind(storageKindName);
        PerformanceTestGate.EnsureEnabled(storageKind);

        var graph = CreateGraph(nodeCount, connectionsPerNode, seed);
        await using var scope = PerformanceStorageScope.Create(storageKind);
        var run = new PerformanceRun(storageKind.ToString(), graph, "storage-operations");
        var nodesByName = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        await run.MeasureEachAsync("create", graph.Nodes, async node =>
        {
            nodesByName[node.Name] = await scope.Storage.Create(node.Name, attributes: node.Attributes).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("connect", graph.Edges, async edge =>
        {
            await scope.Storage.Connect(
                nodesByName[graph.Nodes[edge.SourceIndex].Name],
                nodesByName[graph.Nodes[edge.TargetIndex].Name]).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var sampleCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SAMPLE_COUNT", Math.Min(100, graph.NodeCount));
        var sampleNodeNames = graph.GetSampleNodeNames(sampleCount, seedOffset: 1000);
        var sampleNodes = sampleNodeNames
            .Select(name => nodesByName[name])
            .ToArray();

        await run.MeasureEachAsync("get", sampleNodeNames, async name =>
        {
            var node = await scope.Storage.Get(name).ConfigureAwait(false);
            Assert.IsNotNull(node);
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("get-connected", sampleNodes, async node =>
        {
            await scope.Storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("update", sampleNodeNames, async name =>
        {
            var generatedNode = graph.Nodes.Single(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
            var attributes = new Dictionary<string, string>(generatedNode.Attributes, StringComparer.OrdinalIgnoreCase)
            {
                ["updated"] = "true"
            };

            await scope.Storage.Update(name, attributes).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (scope.Storage is IGraphNodeCatalog catalog)
        {
            await run.MeasureAsync("catalog-get-all", graph.NodeCount, async () =>
            {
                var nodes = await catalog.GetAllNodesAsync().ConfigureAwait(false);
                Assert.AreEqual(graph.NodeCount, nodes.Count);
            }).ConfigureAwait(false);
        }

        await run.MeasureAsync("subgraph-depth-2", 1, async () =>
        {
            var subgraph = await scope.Storage.GetSubgraphAsync(SubgraphQuery.FromRoots([graph.Nodes[0].Name], maxDepth: 2)).ConfigureAwait(false);
            Assert.IsTrue(subgraph.Nodes.Count > 0);
        }).ConfigureAwait(false);

        await run.MeasureAsync("search-group-degree", 1, async () =>
        {
            var matches = await new GraphSearchService(scope.Storage).SearchNodesAsync(new NodeSearchQuery
            {
                Return = ["x"],
                Where = new AllNodeSearchExpression
                {
                    Expressions =
                    [
                        new NodeAttributeSearchExpression
                        {
                            Node = Var("x"),
                            Key = "group",
                            Value = "03"
                        },
                        new NodeDegreeSearchExpression
                        {
                            Node = Var("x"),
                            Operator = SearchOperators.GreaterThanOrEqual,
                            Value = graph.ConnectionsPerNode > 0 ? 1 : 0
                        }
                    ]
                },
                Limit = 50
            }).ConfigureAwait(false);

            Assert.IsTrue(matches.Count > 0);
        }).ConfigureAwait(false);

        run.WriteReport(TestContext);
    }

    [DataTestMethod]
    [DataRow("PerNodeFile", 500, 3, 1729, 4, 1000)]
    [DataRow("BucketedFile", 500, 3, 1729, 4, 1000)]
    [DataRow("SymLink", 500, 3, 1729, 4, 1000)]
    public async Task ConcurrentReadLoad_ShouldRecordResourceUsage(
        string storageKindName,
        int nodeCount,
        int connectionsPerNode,
        int seed,
        int parallelism,
        int operationCount)
    {
        var storageKind = ParseStorageKind(storageKindName);
        PerformanceTestGate.EnsureEnabled(storageKind);

        parallelism = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_PARALLELISM", parallelism);
        operationCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_LOAD_OPERATIONS", operationCount);

        var graph = CreateGraph(nodeCount, connectionsPerNode, seed);
        await using var scope = PerformanceStorageScope.Create(storageKind);
        var run = new PerformanceRun(storageKind.ToString(), graph, "concurrent-read-load");
        var nodesByName = await PopulateGraphAsync(scope.Storage, graph).ConfigureAwait(false);
        var workItems = graph.GetSampleNodeNames(operationCount, seedOffset: 2000);
        var errors = new ConcurrentQueue<Exception>();

        await run.MeasureAsync("parallel-get-and-connections", operationCount, async () =>
        {
            var nextIndex = -1;
            var tasks = Enumerable.Range(0, parallelism)
                .Select(_ => Task.Run(async () =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= workItems.Count)
                        {
                            break;
                        }

                        try
                        {
                            var node = await scope.Storage.Get(workItems[index]).ConfigureAwait(false);
                            Assert.IsNotNull(node);

                            if (index % 3 == 0)
                            {
                                await scope.Storage.GetConnectedNodesAsync(nodesByName[node.Name]).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Enqueue(ex);
                            break;
                        }
                    }
                }))
                .ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (errors.TryPeek(out var error))
        {
            throw new AssertFailedException("Concurrent load operation failed.", error);
        }

        run.WriteReport(TestContext);
    }

    private static async Task<Dictionary<string, Node>> PopulateGraphAsync(IGraphStorage storage, GeneratedGraph graph)
    {
        var nodesByName = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            nodesByName[node.Name] = await storage.Create(node.Name, attributes: node.Attributes).ConfigureAwait(false);
        }

        foreach (var edge in graph.Edges)
        {
            await storage.Connect(
                nodesByName[graph.Nodes[edge.SourceIndex].Name],
                nodesByName[graph.Nodes[edge.TargetIndex].Name]).ConfigureAwait(false);
        }

        return nodesByName;
    }

    private static GeneratedGraph CreateGraph(int nodeCount, int connectionsPerNode, int seed)
    {
        nodeCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_NODE_COUNT", nodeCount);
        connectionsPerNode = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_CONNECTIONS_PER_NODE", connectionsPerNode, minValue: 0);
        seed = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SEED", seed, minValue: 0);
        return GeneratedGraph.Create(nodeCount, connectionsPerNode, seed);
    }

    private static PerformanceStorageKind ParseStorageKind(string value)
    {
        return Enum.TryParse<PerformanceStorageKind>(value, ignoreCase: true, out var storageKind)
            ? storageKind
            : throw new AssertInconclusiveException($"Unknown performance storage kind '{value}'.");
    }

    private static NodeVariableSearchSelector Var(string name)
    {
        return new NodeVariableSearchSelector { Name = name };
    }
}
