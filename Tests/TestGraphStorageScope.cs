using System;
using System.IO;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.PerNodeFileStorage;
using GraphData.PerNodeFileStorage.Options;
using GraphData.SymLinkStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SymLinkStorage;

namespace GraphData.Tests;

internal sealed class TestGraphStorageScope : IAsyncDisposable
{
    private readonly NtfsGraphStorageOptions _options;

    private TestGraphStorageScope(NtfsGraphStorageOptions options)
    {
        _options = options;
        Storage = new SymLinkGraphStorage(
            Options.Create(options),
            new CancellationTokensAccessorMock(),
            NullLogger<SymLinkGraphStorage>.Instance);
    }

    public IGraphStorage Storage { get; }

    public static TestGraphStorageScope Create()
    {
        var options = new NtfsGraphStorageOptions {
            RootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", Guid.NewGuid().ToString("N"))
        };

        return new TestGraphStorageScope(options);
    }

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_options.RootPath) && Directory.Exists(_options.RootPath))
        {
            Directory.Delete(_options.RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
