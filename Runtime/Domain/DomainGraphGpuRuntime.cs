using Abstractions;
using GraphData.Compiler.Adapters;
using GraphData.Compiler.Compilation;
using GraphData.Compiler.Model;
using GraphData.Compiler.Runtime.Chunking;
using GraphData.Compiler.Runtime.Execution;
using GraphData.Core.Models;
using GraphData.Core.Services;

namespace GraphData.Compiler.Runtime.Domain;

/// <summary>
/// Domain-level runtime: obtains a raw subgraph through GraphService, lowers it
/// to a compiler snapshot, executes bounded chunks and merges core-node outputs.
/// </summary>
public sealed class DomainGraphGpuRuntime : IDisposable
{
    private readonly IGraphProgramExecutor executor;
    private readonly GraphProgramCompiler compiler;
    private readonly TypedGraphChunker chunker;
    private readonly bool ownsExecutor;

    public DomainGraphGpuRuntime(
        IGraphProgramExecutor executor,
        GraphProgramCompiler? compiler = null,
        TypedGraphChunker? chunker = null,
        bool ownsExecutor = false)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.compiler = compiler ?? new GraphProgramCompiler();
        this.chunker = chunker ?? new TypedGraphChunker();
        this.ownsExecutor = ownsExecutor;
    }

    public async Task<DomainGraphGpuExecutionResult> ExecuteAsync(
        GraphService graphService,
        IEnumerable<NodeRef> roots,
        int maxDepth,
        Func<global::Node, IReadOnlyList<float>> featureSelector,
        MessagePassingSpecification specification,
        GraphChunkingOptions? chunkingOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphService);
        ArgumentNullException.ThrowIfNull(roots);
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));

        cancellationToken.ThrowIfCancellationRequested();
        var subgraphResult = await graphService.GetSubgraph(roots, maxDepth).ConfigureAwait(false);
        if (subgraphResult.Status != ServiceResultStatus.Ok || subgraphResult.Value is null)
            throw new InvalidOperationException(
                subgraphResult.Error ?? $"Domain graph traversal failed with status '{subgraphResult.Status}'.");

        return Execute(
            subgraphResult.Value,
            featureSelector,
            specification,
            chunkingOptions,
            cancellationToken);
    }

    public DomainGraphGpuExecutionResult Execute(
        Subgraph subgraph,
        Func<global::Node, IReadOnlyList<float>> featureSelector,
        MessagePassingSpecification specification,
        GraphChunkingOptions? chunkingOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subgraph);
        ArgumentNullException.ThrowIfNull(featureSelector);
        ArgumentNullException.ThrowIfNull(specification);

        var snapshot = RawSubgraphSnapshotAdapter.Create(subgraph, featureSelector);
        return ExecuteSnapshot(snapshot, specification, chunkingOptions, cancellationToken);
    }

    public DomainGraphGpuExecutionResult ExecuteSnapshot(
        TypedGraphSnapshot snapshot,
        MessagePassingSpecification specification,
        GraphChunkingOptions? chunkingOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(specification);

        var chunks = chunker.Create(snapshot, chunkingOptions);
        var outputs = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var program = compiler.Compile(chunk.Snapshot, specification);
            var chunkOutput = executor.Execute(program, cancellationToken);
            var chunkValues = chunkOutput.ToArray();
            foreach (var key in chunk.CoreNodeKeys)
            {
                var index = program.GetNodeIndex(key);
                var values = new float[specification.OutputWidth];
                Array.Copy(chunkValues, index * specification.OutputWidth, values, 0, values.Length);
                if (!outputs.TryAdd(key, values))
                    throw new InvalidOperationException($"Node '{key}' was produced by more than one execution chunk.");
            }
        }

        if (outputs.Count != snapshot.Nodes.Count)
            throw new InvalidOperationException("Not every snapshot node received one chunk output.");

        return new DomainGraphGpuExecutionResult(outputs, chunks);
    }

    public void Dispose()
    {
        if (ownsExecutor)
            executor.Dispose();
    }
}
