using Microsoft.AspNetCore.Http;

namespace GraphData.Api.Runtime;

public sealed class HttpContextCancellationTokenAccessor(IHttpContextAccessor httpContextAccessor) : ICancellationTokenAccessor
{
    public IEnumerable<CancellationToken> Tokens
    {
        get
        {
            var token = Token;
            return token.CanBeCanceled
                ? new[] { token }
                : Array.Empty<CancellationToken>();
        }
    }

    public CancellationToken Token => httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
}
