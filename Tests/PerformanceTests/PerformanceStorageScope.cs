using System;
using System.IO;
using System.Threading.Tasks;
using Abstractions;
using Microsoft.Extensions.Options;
using SemanticStorage;

namespace GraphData.Tests.Performance;

public enum PerformanceStorageKind {
    [Obsolete("SemanticFile", true)]
    PerNodeFile,
    [Obsolete("SemanticFile", true)]
    BucketedFile,
    SemanticFile
}

internal sealed class PerformanceStorageScope : IAsyncDisposable {
    private PerformanceStorageScope(PerformanceStorageKind kind, string rootPath, IGraphStorage storage) {
        Kind = kind;
        RootPath = rootPath;
        Storage = storage;
    }

    public PerformanceStorageKind Kind { get; }

    public string RootPath { get; }

    public IGraphStorage Storage { get; }

    public static PerformanceStorageScope Create(PerformanceStorageKind kind, string scenario) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        var rootPath = Path.Combine(
            PerformanceTestGate.GetStorageBaseRoot(),
            "GraphDataPerformanceTests",
            PerformanceTestGate.RunId,
            PerformanceTestGate.SanitizePathSegment(scenario),
            kind.ToString());

        IGraphStorage storage = kind switch {
            //PerformanceStorageKind.PerNodeFile => new PerNodeFileGraphStorage(
            //    Options.Create(new PerNodeFileGraphStorageOptions { RootPath = rootPath }),
            //    NullLogger<PerNodeFileGraphStorage>.Instance),

            //PerformanceStorageKind.BucketedFile => new BucketedFileGraphStorage(
            //    Options.Create(new BucketedFileGraphStorageOptions { RootPath = rootPath }),
            //    NullLogger<BucketedFileGraphStorage>.Instance),

            PerformanceStorageKind.SemanticFile => new SemanticFileGraphStorage(
                Options.Create(new SemanticGraphStorageOptions { RootPath = rootPath }),
                new CancellationTokensAccessorMock()),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown storage kind.")
        };

        return new PerformanceStorageScope(kind, rootPath, storage);
    }

    public async ValueTask DisposeAsync() {
        switch (Storage) {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;

            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
