using System;
using System.IO;
using System.Threading.Tasks;
using Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storage;

namespace GraphData.Tests;

internal sealed class TestGraphStorageScope : IAsyncDisposable
{
    private readonly NtfsGraphStorageOptions _options;

    public static TestGraphStorageScope Create() {
        return new TestGraphStorageScope(new NtfsGraphStorageOptions {
            RootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", Guid.NewGuid().ToString("N"))
        });
    }

    public TestGraphStorageScope(NtfsGraphStorageOptions options)
    {
        _options = options;
        Storage = new SymLinkGraphStorage(
            Options.Create(options),
            new CancellationTokensAccessorMock(),
            NullLogger<SymLinkGraphStorage>.Instance);
    }

    internal IGraphStorage Storage { get; }

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_options.RootPath) && Directory.Exists(_options.RootPath))
            Directory.Delete(_options.RootPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}
