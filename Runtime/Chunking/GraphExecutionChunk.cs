using System.Collections.ObjectModel;
using GraphData.Compiler.Model;

namespace GraphData.Compiler.Runtime.Chunking;

public sealed class GraphExecutionChunk
{
    public TypedGraphSnapshot Snapshot { get; }
    public IReadOnlyList<string> CoreNodeKeys { get; }

    internal GraphExecutionChunk(TypedGraphSnapshot snapshot, IEnumerable<string> coreNodeKeys)
    {
        Snapshot = snapshot;
        CoreNodeKeys = new ReadOnlyCollection<string>(coreNodeKeys.ToArray());
    }
}
