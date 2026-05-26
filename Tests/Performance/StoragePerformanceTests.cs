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
public sealed class StoragePerformanceTests {
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    //[DataRow(PerformanceStorageKind.PerNodeFile, 250, 2, 1729)]
    //[DataRow(PerformanceStorageKind.BucketedFile, 250, 2, 1729)]
    [DataRow(PerformanceStorageKind.SymLink, 250, 2, 1729)]
    public async Task StorageOperations_ShouldRecordDetailedTimings(
        PerformanceStorageKind storageKind,
        int nodeCount,
        int connectionsPerNode,
        int seed) {
        PerformanceTestGate.EnsureEnabled(storageKind);

        var graph = CreateGraph(nodeCount, connectionsPerNode, seed);
        AssertGraphContainsCycleWhenPossible(graph);

        const string scenario = "storage-operations";
        await using var scope = PerformanceStorageScope.Create(storageKind, scenario);
        AssertStorageRoot(scope, scenario);

        var run = new PerformanceRun(storageKind.ToString(), graph, scenario, scope.RootPath);
        var nodesByName = new Dictionary<NodeGlobalId, Node>();

        await run.MeasureEachAsync("create", graph.Nodes, async node => {
            nodesByName[node.Path] = (await scope.Storage.Create(node.Path.Last().Value, attributes: node.Attributes).ConfigureAwait(false)).Value!;
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("connect", graph.Edges, async edge => {
            await scope.Storage.Connect(
                nodesByName[graph.Nodes[edge.SourceIndex].Path].GlobalId,
                nodesByName[graph.Nodes[edge.TargetIndex].Path].GlobalId).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var sampleCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SAMPLE_COUNT", Math.Min(100, graph.NodeCount));
        var sampleNodeNames = graph.GetSampleNodeNames(sampleCount, seedOffset: 1000);
        var sampleNodes = sampleNodeNames
            .Select(name => nodesByName[name])
            .ToArray();

        await run.MeasureEachAsync("get", sampleNodeNames, async name => {
            var node = await scope.Storage.Get(name).ConfigureAwait(false);
            Assert.IsNotNull(node);
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("get-connected", sampleNodes, async node => {
            await scope.Storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await run.MeasureEachAsync("update", sampleNodeNames, async name => {
            var generatedNode = graph.Nodes.Single(node => node.Path.SequenceEqual(name));
            var attributes = new Dictionary<string, string>(generatedNode.Attributes, StringComparer.OrdinalIgnoreCase) {
                ["updated"] = "true"
            };

            await scope.Storage.Update(name, attributes).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (scope.Storage is IGraphNodeCatalog catalog) {
            await run.MeasureAsync("catalog-get-all", graph.NodeCount, async () => {
                var nodes = await catalog.GetAllNodesAsync().ConfigureAwait(false);
                Assert.AreEqual(graph.NodeCount, nodes.Count);
            }).ConfigureAwait(false);
        }

        await run.MeasureAsync("subgraph-depth-2", 1, async () => {
            var subgraph = (await scope.Storage.GetSubgraphAsync(new SubgraphQuery { Nodes = [graph.Nodes[0].Path], MaxDepth = 2 }).ConfigureAwait(false)).Value!;
            Assert.IsTrue(subgraph.Nodes.Count > 0);
        }).ConfigureAwait(false);

        await run.MeasureAsync("search-group-degree", 1, async () => {
            var matches = await new GraphSearchService(scope.Storage).SearchNodesStreamAsync(new NodeSearchQuery {
                Return = ["x"],
                Where = new AllNodeSearchExpression {
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
            }).ToArrayAsync();

            Assert.IsTrue(matches.Length > 0);
        }).ConfigureAwait(false);

        run.WriteReport(scope, TestContext);
    }

    [DataTestMethod]
    //[DataRow(PerformanceStorageKind.PerNodeFile, 500, 3, 1729, 4, 1000)]
    //[DataRow(PerformanceStorageKind.BucketedFile, 500, 3, 1729, 4, 1000)]
    [DataRow(PerformanceStorageKind.SymLink, 500, 3, 1729, 4, 1000)]
    public async Task ConcurrentReadLoad_ShouldRecordResourceUsage(
        PerformanceStorageKind storageKind,
        int nodeCount,
        int connectionsPerNode,
        int seed,
        int parallelism,
        int operationCount) {
        PerformanceTestGate.EnsureEnabled(storageKind);

        parallelism = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_PARALLELISM", parallelism);
        operationCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_LOAD_OPERATIONS", operationCount);

        var graph = CreateGraph(nodeCount, connectionsPerNode, seed);
        AssertGraphContainsCycleWhenPossible(graph);

        const string scenario = "concurrent-read-load";
        await using var scope = PerformanceStorageScope.Create(storageKind, scenario);
        AssertStorageRoot(scope, scenario);

        var run = new PerformanceRun(storageKind.ToString(), graph, scenario, scope.RootPath);
        var nodesByName = await PopulateGraphAsync(scope.Storage, graph).ConfigureAwait(false);
        var workItems = graph.GetSampleNodeNames(operationCount, seedOffset: 2000);
        var errors = new ConcurrentQueue<Exception>();

        await run.MeasureAsync("parallel-get-and-connections", operationCount, async () => {
            var nextIndex = -1;
            var tasks = Enumerable.Range(0, parallelism)
                .Select(_ => Task.Run(async () => {
                    while (true) {
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= workItems.Count) {
                            break;
                        }

                        try {
                            var node = (await scope.Storage.Get(workItems[index]).ConfigureAwait(false)).Value!;
                            Assert.IsNotNull(node);

                            if (index % 3 == 0) {
                                await scope.Storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
                            }
                        } catch (Exception ex) {
                            errors.Enqueue(ex);
                            break;
                        }
                    }
                }))
                .ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (errors.TryPeek(out var error)) {
            throw new AssertFailedException("Concurrent load operation failed.", error);
        }

        run.WriteReport(scope, TestContext);
    }

    [DataTestMethod]
    //[DataRow(PerformanceStorageKind.PerNodeFile, 500, 3, 1729)]
    //[DataRow(PerformanceStorageKind.BucketedFile, 500, 3, 1729)]
    [DataRow(PerformanceStorageKind.SymLink, 500, 3, 1729)]
    public async Task RandomSubgraphReads_ShouldRecordDetailedTimings(
        PerformanceStorageKind storageKind,
        int nodeCount,
        int connectionsPerNode,
        int seed) {
        PerformanceTestGate.EnsureEnabled(storageKind);

        var graph = CreateGraph(nodeCount, connectionsPerNode, seed);
        AssertGraphContainsCycleWhenPossible(graph);

        const string scenario = "subgraph-random-reads";
        await using var scope = PerformanceStorageScope.Create(storageKind, scenario);
        AssertStorageRoot(scope, scenario);

        var run = new PerformanceRun(storageKind.ToString(), graph, scenario, scope.RootPath);
        await PopulateGraphAsync(scope.Storage, graph).ConfigureAwait(false);

        var sampleCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SUBGRAPH_SAMPLE_COUNT", Math.Min(25, graph.NodeCount));
        var depths = PerformanceTestGate.GetIntList("GRAPH_DATA_PERF_SUBGRAPH_DEPTHS", [1, 2, 3], minValue: 0);
        var multiRootCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SUBGRAPH_ROOT_COUNT", 3);
        var singleRootQueries = graph.GetSampleNodeNames(sampleCount, seedOffset: 3000)
            .Select(static root => new SubgraphQueryInput([root]))
            .ToArray();
        var multiRootQueries = CreateMultiRootSubgraphQueries(graph, sampleCount, multiRootCount);

        foreach (var depth in depths) {
            await run.MeasureEachAsync(
                $"subgraph-single-root-depth-{depth}",
                singleRootQueries,
                query => ReadSubgraphNodeCountAsync(scope.Storage, query, depth),
                valueName: "nodes").ConfigureAwait(false);

            await run.MeasureEachAsync(
                $"subgraph-{multiRootCount}-roots-depth-{depth}",
                multiRootQueries,
                query => ReadSubgraphNodeCountAsync(scope.Storage, query, depth),
                valueName: "nodes").ConfigureAwait(false);
        }

        run.WriteReport(scope, TestContext);
    }

    private static async Task<Dictionary<NodeGlobalId, Node>> PopulateGraphAsync(IGraphStorage storage, GeneratedGraph graph) {
        var nodesByName = new Dictionary<NodeGlobalId, Node>();
        foreach (var node in graph.Nodes) {
            nodesByName[node.Path] = (await storage.Create(node.Path.Single().Value, attributes: node.Attributes).ConfigureAwait(false)).Value!;
        }

        foreach (var edge in graph.Edges) {
            await storage.Connect(
                nodesByName[graph.Nodes[edge.SourceIndex].Path].GlobalId,
                nodesByName[graph.Nodes[edge.TargetIndex].Path].GlobalId).ConfigureAwait(false);
        }

        return nodesByName;
    }

    private static async Task<int> ReadSubgraphNodeCountAsync(
        IGraphStorage storage,
        SubgraphQueryInput input,
        int depth) {
        var subgraph = (await storage.GetSubgraphAsync(new SubgraphQuery { Nodes = input.Roots, MaxDepth = depth } ).ConfigureAwait(false)).Value!;
        Assert.IsTrue(subgraph.Nodes.Count > 0);
        return subgraph.Nodes.Count;
    }

    private static IReadOnlyCollection<SubgraphQueryInput> CreateMultiRootSubgraphQueries(
        GeneratedGraph graph,
        int sampleCount,
        int rootCount) {
        rootCount = Math.Clamp(rootCount, 1, graph.NodeCount);
        var names = graph.GetSampleNodeNames(sampleCount * rootCount, seedOffset: 4000);
        return Enumerable.Range(0, sampleCount)
            .Select(index => new SubgraphQueryInput(
                names
                    .Skip(index * rootCount)
                    .Take(rootCount)
                    .Distinct()
                    .ToArray()))
            .ToArray();
    }

    private static GeneratedGraph CreateGraph(int nodeCount, int connectionsPerNode, int seed) {
        nodeCount = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_NODE_COUNT", nodeCount);
        connectionsPerNode = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_CONNECTIONS_PER_NODE", connectionsPerNode, minValue: 0);
        seed = PerformanceTestGate.GetInt("GRAPH_DATA_PERF_SEED", seed, minValue: 0);
        return GeneratedGraph.Create(nodeCount, connectionsPerNode, seed);
    }

    private static void AssertGraphContainsCycleWhenPossible(GeneratedGraph graph) {
        if (graph.NodeCount >= 3 && graph.ConnectionsPerNode > 0) {
            Assert.IsTrue(graph.ContainsCycle, "Generated performance graph must contain cycles.");
        }
    }

    private static void AssertStorageRoot(PerformanceStorageScope scope, string scenario) {
        var expectedBaseRoot = PerformanceTestGate.GetStorageBaseRoot();
        StringAssert.StartsWith(scope.RootPath, expectedBaseRoot);
        StringAssert.Contains(scope.RootPath, PerformanceTestGate.RunId);
        StringAssert.Contains(scope.RootPath, scenario);
        StringAssert.Contains(scope.RootPath, scope.Kind.ToString());
    }

    private static NodeVariableSearchSelector Var(string name) {
        return new NodeVariableSearchSelector { Name = name };
    }

    private sealed record SubgraphQueryInput(IReadOnlyCollection<NodeGlobalId> Roots);
}
