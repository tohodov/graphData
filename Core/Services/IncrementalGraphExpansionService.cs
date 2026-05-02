using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class IncrementalGraphExpansionService(IGraphStorage storage)
{
    private readonly IGraphStorage _storage = storage;

    public async Task<IncrementalExpansionResult> ApplyStepAsync(IncrementalExpansionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var createdNodes = new List<string>();
        var updatedNodes = new List<string>();
        var connectedPairs = new List<string>();

        foreach (var upsert in request.Upserts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(upsert.Name);

            var existing = await _storage.Get(upsert.Name);
            if (existing is null)
            {
                await _storage.Create(upsert.Name, null, upsert.Attributes);
                createdNodes.Add(upsert.Name);
                continue;
            }

            if (upsert.Attributes is null)
            {
                continue;
            }

            var attributesToSave = upsert.MergeAttributes
                ? existing.Attributes.Concat(upsert.Attributes)
                    .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(upsert.Attributes, StringComparer.OrdinalIgnoreCase);

            await _storage.Update(upsert.Name, attributesToSave);
            updatedNodes.Add(upsert.Name);
        }

        foreach (var connection in request.Connections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connection.SourceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(connection.TargetName);

            var source = await _storage.Get(connection.SourceName) ?? await _storage.Create(connection.SourceName);
            var target = await _storage.Get(connection.TargetName) ?? await _storage.Create(connection.TargetName);
            await _storage.Connect(source, target);
            connectedPairs.Add($"{source.Name}<->{target.Name}");
        }

        var subgraph = await _storage.GetSubgraphAsync(new SubgraphQuery
        {
            RootNodeIds = request.RootContextNodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            MaxDepth = request.ContextDepth,
            IncludeDisconnectedRoots = true
        });

        return new IncrementalExpansionResult
        {
            CreatedNodes = createdNodes,
            UpdatedNodes = updatedNodes,
            ConnectedPairs = connectedPairs,
            ContextSubgraph = subgraph
        };
    }
}
