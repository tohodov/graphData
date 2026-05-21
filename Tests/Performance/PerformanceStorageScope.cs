using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GraphData.BucketedFileStorage;
using GraphData.BucketedFileStorage.Options;
using GraphData.Core.Abstractions;
using GraphData.PerNodeFileStorage;
using GraphData.PerNodeFileStorage.Options;
using GraphData.SymLinkStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SymLinkStorage;

namespace GraphData.Tests.Performance;

internal enum PerformanceStorageKind
{
    PerNodeFile,
    BucketedFile,
    SymLink
}

internal sealed class PerformanceStorageScope : IAsyncDisposable
{
    private PerformanceStorageScope(PerformanceStorageKind kind, string rootPath, IGraphStorage storage)
    {
        Kind = kind;
        RootPath = rootPath;
        Storage = storage;
    }

    public PerformanceStorageKind Kind { get; }

    public string RootPath { get; }

    public IGraphStorage Storage { get; }

    public static PerformanceStorageScope Create(PerformanceStorageKind kind, string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        var rootPath = Path.Combine(
            PerformanceTestGate.GetStorageBaseRoot(),
            "GraphDataPerformanceTests",
            SanitizePathSegment(scenario),
            kind.ToString(),
            Guid.NewGuid().ToString("N"));

        IGraphStorage storage = kind switch
        {
            PerformanceStorageKind.PerNodeFile => new PerNodeFileGraphStorage(
                Options.Create(new PerNodeFileGraphStorageOptions { RootPath = rootPath }),
                NullLogger<PerNodeFileGraphStorage>.Instance),

            PerformanceStorageKind.BucketedFile => new BucketedFileGraphStorage(
                Options.Create(new BucketedFileGraphStorageOptions { RootPath = rootPath }),
                NullLogger<BucketedFileGraphStorage>.Instance),

            PerformanceStorageKind.SymLink => new SymLinkGraphStorage(
                Options.Create(new NtfsGraphStorageOptions { RootPath = rootPath }),
                new CancellationTokensAccessorMock(),
                NullLogger<SymLinkGraphStorage>.Instance),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown storage kind.")
        };

        return new PerformanceStorageScope(kind, rootPath, storage);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            switch (Storage)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;

                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        finally
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray();

        return new string(chars);
    }
}
