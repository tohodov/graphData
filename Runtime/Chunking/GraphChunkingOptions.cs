namespace GraphData.Compiler.Runtime.Chunking;

public sealed class GraphChunkingOptions
{
    /// <summary>
    /// Maximum count of target nodes whose output is collected from one dispatch.
    /// Source nodes needed by their incoming relations are added as a read-only halo.
    /// </summary>
    public int MaxCoreNodes { get; init; } = 65_536;
}
