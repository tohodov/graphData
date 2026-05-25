using System.Runtime.CompilerServices;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class NodeService(IGraphStorage storage, GraphSearchService searchService) {
    private readonly IGraphStorage storage = storage;
    private readonly GraphSearchService searchService = searchService;

    public async Task<ServiceResult<Node>> Get(NodePath path) {
        var node = await storage.Get(path);
        if (node is null)
            return ServiceResult<Node>.NotFound();
        return ServiceResult<Node>.Ok(node);
    }

    public async Task<ServiceResult<Node>> Create(string name, NodePath? parentId, IDictionary<string, string>? attributes) {
        if (!NodeNameValidator.TryValidateSegment(name, "Node name", out var validationError))
            return ServiceResult<Node>.BadRequest(validationError);
        Node? parent = null;
        if (parentId is { Count: > 0 }) {
            parent = await storage.Get(parentId);
            if (parent is null)
                return ServiceResult<Node>.NotFound($"Parent node '{parentId}' was not found.");
        }
        var created = await storage.Create(name, parent, attributes);
        return ServiceResult<Node>.Ok(created);
    }

    public async Task<ServiceResult> UpdateNodeAsync(NodePath path, IDictionary<string, string> attributes) {
        var node = await storage.Get(path);
        if (node is null)
            return ServiceResult.NotFound();
        node.Attributes = attributes.ToDictionary();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> Delete(NodePath path) {
        var node = await storage.Get(path);//TODO оптимизировать двойное чтение
        if (node is null)
            return ServiceResult.NotFound();
        await storage.Delete(path);//TODO оптимизировать двойное чтение
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ConnectNodesAsync(NodePath firstId, NodePath secondId) {
        if (firstId.SequenceEqual(secondId))
            return ServiceResult.BadRequest("SourcePath and TargetPath must be different.");
        var first = await storage.Get(firstId);
        var second = await storage.Get(secondId);
        if (first is null || second is null)
            return ServiceResult.NotFound();
        try {
            await storage.Connect(first, second);
        } catch (Exception ex) {
            return ServiceResult.InternalServerError(ex.ToString());
        }
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<Subgraph>> GetSubgraphAsync(SubgraphQuery query) {
        if (query.Nodes.Count == 0)
            return ServiceResult<Subgraph>.Ok(new Subgraph { Nodes = [] });
        var subgraph = await storage.GetSubgraphAsync(query);
        return ServiceResult<Subgraph>.Ok(subgraph);
    }

    public async IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(NodeSearchQuery request, [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var match in searchService.SearchNodesStreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
            yield return match;
    }
}

public enum ServiceResultStatus {
    Ok,
    BadRequest,
    NotFound,
    InternalServerError,//TODO подумать и переделать перехват исключений
}

public sealed record ServiceResult(ServiceResultStatus Status, string? Error = null) {
    public static ServiceResult Ok() => new(ServiceResultStatus.Ok);
    public static ServiceResult BadRequest(string? error = null) => new(ServiceResultStatus.BadRequest, error);
    public static ServiceResult NotFound(string? error = null) => new(ServiceResultStatus.NotFound, error);
    public static ServiceResult InternalServerError(string? error = null) => new(ServiceResultStatus.InternalServerError, error);
}

public sealed record ServiceResult<T>(ServiceResultStatus Status, T? Value = default, string? Error = null) {
    public static ServiceResult<T> Ok(T value) => new(ServiceResultStatus.Ok, value);
    public static ServiceResult<T> BadRequest(string? error = null) => new(ServiceResultStatus.BadRequest, Error: error);
    public static ServiceResult<T> NotFound(string? error = null) => new(ServiceResultStatus.NotFound, Error: error);
    public static ServiceResult<T> InternalServerError(string? error = null) => new(ServiceResultStatus.InternalServerError, Error: error);
}
