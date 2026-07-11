using System.Collections.ObjectModel;
using GraphData.Compiler.Runtime.Chunking;

namespace GraphData.Compiler.Runtime.Domain;

public sealed class DomainGraphGpuExecutionResult
{
    public IReadOnlyDictionary<string, float[]> NodeOutputs { get; }
    public IReadOnlyList<GraphExecutionChunk> Chunks { get; }

    internal DomainGraphGpuExecutionResult(
        IDictionary<string, float[]> nodeOutputs,
        IEnumerable<GraphExecutionChunk> chunks)
    {
        NodeOutputs = new ReadOnlyDictionary<string, float[]>(
            nodeOutputs.ToDictionary(
                static pair => pair.Key,
                static pair => (float[])pair.Value.Clone(),
                StringComparer.Ordinal));
        Chunks = new ReadOnlyCollection<GraphExecutionChunk>(chunks.ToArray());
    }
}
