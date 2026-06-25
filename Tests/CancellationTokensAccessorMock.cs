using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Abstractions;

internal class CancellationTokensAccessorMock : ICancellationTokenAccessor {
    public IEnumerable<CancellationToken> Tokens => Array.Empty<CancellationToken>();
    public CancellationToken Token => CancellationToken.None;
}
internal class LazyList<T> : List<T>, ILazyCollection<T> { //TODO переписать на полноценную ленивую коллекцию
    public LazyList() { }
    public LazyList(params IEnumerable<T> source) : base(source) { }
    public IAsyncEnumerable<T> Traverse() => this.ToAsyncEnumerable();
}