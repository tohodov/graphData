namespace Abstractions;

internal interface IGraphNodeStream
{
    IAsyncEnumerable<NodeState> EnumerateNodesAsync(CancellationToken cancellationToken = default);
}
