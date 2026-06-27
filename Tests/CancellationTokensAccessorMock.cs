using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Abstractions;

internal class CancellationTokensAccessorMock : ICancellationTokenAccessor {
    public IEnumerable<CancellationToken> Tokens => Array.Empty<CancellationToken>();
    public CancellationToken Token => CancellationToken.None;
}