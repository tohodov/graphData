public interface ICancellationTokenAccessor {
    IEnumerable<CancellationToken> Tokens { get; }
    CancellationToken Token => new CancellationToken(Tokens.Any(x => x.IsCancellationRequested));
}