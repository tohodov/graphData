using GraphData.Compiler.Model;

namespace GraphData.Compiler.Runtime.Chunking;

/// <summary>
/// Partitions output rows by target node. Every chunk contains its core targets
/// and the source-node halo needed to evaluate their incoming relations.
/// </summary>
public sealed class TypedGraphChunker
{
    public IReadOnlyList<GraphExecutionChunk> Create(
        TypedGraphSnapshot snapshot,
        GraphChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new GraphChunkingOptions();
        if (options.MaxCoreNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCoreNodes must be greater than zero.");

        var nodes = snapshot.Nodes
            .OrderBy(static node => node.Key, StringComparer.Ordinal)
            .ToArray();
        var duplicateKey = nodes
            .GroupBy(static node => node.Key, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicateKey is not null)
            throw new ArgumentException($"Node key '{duplicateKey.Key}' is duplicated.", nameof(snapshot));

        var nodeKeys = new HashSet<string>(nodes.Select(static node => node.Key), StringComparer.Ordinal);
        var missingEndpoint = snapshot.Relations.FirstOrDefault(relation =>
            !nodeKeys.Contains(relation.SourceKey) || !nodeKeys.Contains(relation.TargetKey));
        if (missingEndpoint is not null)
            throw new ArgumentException(
                $"Relation '{missingEndpoint.Key}' has an endpoint outside the snapshot.",
                nameof(snapshot));

        var chunks = new List<GraphExecutionChunk>();
        for (var offset = 0; offset < nodes.Length; offset += options.MaxCoreNodes)
        {
            var coreNodes = nodes.Skip(offset).Take(options.MaxCoreNodes).ToArray();
            var coreKeys = new HashSet<string>(
                coreNodes.Select(static node => node.Key),
                StringComparer.Ordinal);
            var relations = snapshot.Relations
                .Where(relation => coreKeys.Contains(relation.TargetKey))
                .OrderBy(static relation => relation.TypeKey, StringComparer.Ordinal)
                .ThenBy(static relation => relation.TargetKey, StringComparer.Ordinal)
                .ThenBy(static relation => relation.SourceKey, StringComparer.Ordinal)
                .ThenBy(static relation => relation.Key, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
            var inputKeys = new HashSet<string>(coreKeys, StringComparer.Ordinal);
            foreach (var relation in relations)
                inputKeys.Add(relation.SourceKey);

            var chunkNodes = nodes
                .Where(node => inputKeys.Contains(node.Key))
                .Select(Clone)
                .ToArray();
            chunks.Add(new GraphExecutionChunk(
                new TypedGraphSnapshot(chunkNodes, relations),
                coreNodes.Select(static node => node.Key)));
        }

        return chunks;
    }

    private static TypedNodeSnapshot Clone(TypedNodeSnapshot node) =>
        new(node.Key, node.TypeKeys.ToArray(), node.Features.ToArray());

    private static TypedRelationSnapshot Clone(TypedRelationSnapshot relation) =>
        new(relation.Key, relation.TypeKey, relation.SourceKey, relation.TargetKey, relation.Weight);
}
