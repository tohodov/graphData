using System.Text.Json;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

internal sealed class GraphBackupService {
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };

    readonly GraphService graph;

    public GraphBackupService(GraphService graph) {
        this.graph = graph;
    }

    public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var subgraphResult = await graph.GetSubgraph([], int.MaxValue).ConfigureAwait(false);
        if (subgraphResult.Status != ServiceResultStatus.Ok || subgraphResult.Value is null)
            throw new InvalidOperationException(subgraphResult.Error ?? "Could not read graph for backup.");

        var document = CreateDocument(subgraphResult.Value, cancellationToken);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string filePath, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        var document = await JsonSerializer.DeserializeAsync<GraphBackupDocument>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Graph backup file is empty.");

        ValidateDocument(document);

        var nodes = document.Nodes
            .OrderBy(static node => node.Path.Length)
            .ThenBy(static node => ToPathString(node.Path), StringComparer.Ordinal)
            .ToArray();
        foreach (var node in nodes) {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureNodeAsync(node).ConfigureAwait(false);
        }

        foreach (var edge in document.Edges
            .OrderBy(static edge => ToPathString(edge.Source), StringComparer.Ordinal)
            .ThenBy(static edge => ToPathString(edge.Target), StringComparer.Ordinal)) {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await graph.ConnectNodesAsync(ToNodePath(edge.Source), ToNodePath(edge.Target)).ConfigureAwait(false);
            ThrowIfFailed(result, $"Could not restore edge '{ToPathString(edge.Source)}' - '{ToPathString(edge.Target)}'.");
        }
    }

    private static GraphBackupDocument CreateDocument(Subgraph subgraph, CancellationToken cancellationToken) {
        var nodes = subgraph.Nodes
            .OrderBy(static node => ToPathString(ToSegments(node.GlobalId)), StringComparer.Ordinal)
            .Select(static node => new GraphBackupNode {
                Path = ToSegments(node.GlobalId),
                Attributes = new Dictionary<string, string>(node.Attributes, StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();

        var nodeIds = nodes
            .Select(static node => ToPathString(node.Path))
            .ToHashSet(StringComparer.Ordinal);
        var edges = new Dictionary<GraphBackupEdgeKey, GraphBackupEdge>();
        foreach (var node in subgraph.Nodes) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in node.Edges) {
                var source = ToSegments(edge.Node1.GlobalId);
                var target = ToSegments(edge.Node2.GlobalId);
                if (!nodeIds.Contains(ToPathString(source)) || !nodeIds.Contains(ToPathString(target)))
                    continue;
                if (source.SequenceEqual(target))
                    continue;
                if (IsHierarchyEdge(source, target))
                    continue;

                var key = GraphBackupEdgeKey.Create(source, target);
                var sourcePath = ToPathString(source);
                edges.TryAdd(key, new GraphBackupEdge {
                    Source = key.SourcePath == sourcePath ? source : target,
                    Target = key.SourcePath == sourcePath ? target : source
                });
            }
        }

        return new GraphBackupDocument {
            Version = CurrentVersion,
            Nodes = nodes.ToList(),
            Edges = edges.Values
                .OrderBy(static edge => ToPathString(edge.Source), StringComparer.Ordinal)
                .ThenBy(static edge => ToPathString(edge.Target), StringComparer.Ordinal)
                .ToList()
        };
    }

    private async Task EnsureNodeAsync(GraphBackupNode node) {
        var path = ToNodePath(node.Path);
        var existing = await graph.GetNode(path).ConfigureAwait(false);
        if (existing.Status == ServiceResultStatus.NotFound) {
            var result = await graph.CreateNode(
                    new NodeLocalId(node.Path[^1]),
                    ParentPath(node.Path),
                    attributes: node.Attributes.Count == 0 ? null : node.Attributes)
                .ConfigureAwait(false);
            ThrowIfFailed(result, $"Could not restore node '{ToPathString(node.Path)}'.");
        } else {
            ThrowIfFailed(existing, $"Could not read node '{ToPathString(node.Path)}'.");
        }

        var updateResult = await graph.UpdateNode(path, node.Attributes).ConfigureAwait(false);
        ThrowIfFailed(updateResult, $"Could not restore node attributes for '{ToPathString(node.Path)}'.");
    }

    private static void ValidateDocument(GraphBackupDocument document) {
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported graph backup version '{document.Version}'.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in document.Nodes) {
            if (node.Path is null || node.Path.Length == 0)
                throw new InvalidDataException("Graph backup contains a node with an empty path.");
            if (node.Path.Any(static segment => string.IsNullOrWhiteSpace(segment)))
                throw new InvalidDataException($"Graph backup contains an invalid node path '{ToPathString(node.Path)}'.");
            if (node.Attributes is null)
                throw new InvalidDataException($"Graph backup node '{ToPathString(node.Path)}' has no attributes object.");
            if (!paths.Add(ToPathString(node.Path)))
                throw new InvalidDataException($"Graph backup contains duplicate node '{ToPathString(node.Path)}'.");
        }

        foreach (var edge in document.Edges) {
            if (edge.Source is null || edge.Target is null || edge.Source.Length == 0 || edge.Target.Length == 0)
                throw new InvalidDataException("Graph backup contains an edge with an empty endpoint path.");
            if (edge.Source.Any(static segment => string.IsNullOrWhiteSpace(segment)))
                throw new InvalidDataException($"Graph backup contains an invalid edge source '{ToPathString(edge.Source)}'.");
            if (edge.Target.Any(static segment => string.IsNullOrWhiteSpace(segment)))
                throw new InvalidDataException($"Graph backup contains an invalid edge target '{ToPathString(edge.Target)}'.");
            if (!paths.Contains(ToPathString(edge.Source)))
                throw new InvalidDataException($"Graph backup edge source '{ToPathString(edge.Source)}' was not found among nodes.");
            if (!paths.Contains(ToPathString(edge.Target)))
                throw new InvalidDataException($"Graph backup edge target '{ToPathString(edge.Target)}' was not found among nodes.");
        }
    }

    private static string[] ToSegments(InternalId id) =>
        id.Select(static segment => segment.ToString()).ToArray();

    private static NodePath ToNodePath(string[] path) => new(path);

    private static NodePath? ParentPath(string[] path) =>
        path.Length <= 1
            ? null
            : new NodePath(path.Take(path.Length - 1));

    private static string ToPathString(string[] path) => string.Join("/", path);

    private static bool IsHierarchyEdge(string[] source, string[] target) =>
        IsDirectChild(source, target) || IsDirectChild(target, source);

    private static bool IsDirectChild(string[] child, string[] parent) {
        if (child.Length != parent.Length + 1)
            return false;

        for (var index = 0; index < parent.Length; index++)
            if (!string.Equals(child[index], parent[index], StringComparison.Ordinal))
                return false;

        return true;
    }

    private static void ThrowIfFailed(ServiceResult result, string message) {
        if (result.Status == ServiceResultStatus.Ok)
            return;

        throw new InvalidOperationException(result.Error is null ? message : $"{message} {result.Error}");
    }

    private static void ThrowIfFailed<T>(ServiceResult<T> result, string message) {
        if (result.Status == ServiceResultStatus.Ok)
            return;

        throw new InvalidOperationException(result.Error is null ? message : $"{message} {result.Error}");
    }

    private sealed class GraphBackupDocument {
        public int Version { get; init; }
        public List<GraphBackupNode> Nodes { get; init; } = [];
        public List<GraphBackupEdge> Edges { get; init; } = [];
    }

    private sealed class GraphBackupNode {
        public string[] Path { get; init; } = [];
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GraphBackupEdge {
        public string[] Source { get; init; } = [];
        public string[] Target { get; init; } = [];
    }

    private readonly record struct GraphBackupEdgeKey(string SourcePath, string TargetPath) {
        public static GraphBackupEdgeKey Create(string[] source, string[] target) {
            var sourcePath = ToPathString(source);
            var targetPath = ToPathString(target);
            return string.Compare(sourcePath, targetPath, StringComparison.Ordinal) <= 0
                ? new GraphBackupEdgeKey(sourcePath, targetPath)
                : new GraphBackupEdgeKey(targetPath, sourcePath);
        }
    }
}
