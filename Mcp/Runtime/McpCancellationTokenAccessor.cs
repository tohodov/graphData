namespace GraphData.Mcp.Runtime;

internal sealed class McpCancellationTokenAccessor : ICancellationTokenAccessor
{
    public IEnumerable<CancellationToken> Tokens => [CancellationToken.None];
}
