using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Performance;

internal sealed class PerformanceRun
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly List<PerformanceSample> _samples = [];

    public PerformanceRun(string storageKind, GeneratedGraph graph, string scenario, string storageRootPath)
    {
        StorageKind = storageKind;
        Graph = graph;
        Scenario = scenario;
        StorageRootPath = storageRootPath;
    }

    public string StorageKind { get; }

    public GeneratedGraph Graph { get; }

    public string Scenario { get; }

    public string StorageRootPath { get; }

    public async Task MeasureAsync(string operation, int count, Func<Task> work)
    {
        var start = ResourceSnapshot.Capture();
        var stopwatch = Stopwatch.StartNew();
        await work().ConfigureAwait(false);
        stopwatch.Stop();
        var finish = ResourceSnapshot.Capture();

        _samples.Add(PerformanceSample.Create(operation, count, stopwatch.Elapsed, start, finish, null));
    }

    public async Task MeasureEachAsync<T>(string operation, IReadOnlyCollection<T> items, Func<T, Task> work)
    {
        var itemDurations = new List<TimeSpan>(items.Count);
        var start = ResourceSnapshot.Capture();
        var stopwatch = Stopwatch.StartNew();

        foreach (var item in items)
        {
            var itemStopwatch = Stopwatch.StartNew();
            await work(item).ConfigureAwait(false);
            itemStopwatch.Stop();
            itemDurations.Add(itemStopwatch.Elapsed);
        }

        stopwatch.Stop();
        var finish = ResourceSnapshot.Capture();

        _samples.Add(PerformanceSample.Create(operation, items.Count, stopwatch.Elapsed, start, finish, itemDurations));
    }

    public void WriteReport(TestContext context)
    {
        context.WriteLine($"Performance scenario: {Scenario}");
        context.WriteLine($"Storage: {StorageKind}");
        context.WriteLine($"Storage root: {StorageRootPath}");
        context.WriteLine($"Graph: nodes={Graph.NodeCount}, connectionsPerNode={Graph.ConnectionsPerNode}, edges={Graph.Edges.Count}, seed={Graph.Seed}, containsCycle={Graph.ContainsCycle}");
        context.WriteLine("operation | count | elapsed ms | avg us/op | min us | p50 us | p95 us | max us | cpu ms | cpu % | allocated MB | managed delta MB | working set delta MB | private delta MB | GC");

        foreach (var sample in _samples)
        {
            context.WriteLine(
                string.Join(
                    " | ",
                    sample.Operation,
                    sample.Count.ToString(CultureInfo.InvariantCulture),
                    Format(sample.ElapsedMilliseconds),
                    Format(sample.AverageMicrosecondsPerOperation),
                    Format(sample.MinMicroseconds),
                    Format(sample.P50Microseconds),
                    Format(sample.P95Microseconds),
                    Format(sample.MaxMicroseconds),
                    Format(sample.CpuMilliseconds),
                    Format(sample.CpuPercent),
                    Format(sample.AllocatedMegabytes),
                    Format(sample.ManagedMemoryDeltaMegabytes),
                    Format(sample.WorkingSetDeltaMegabytes),
                    Format(sample.PrivateMemoryDeltaMegabytes),
                    $"{sample.Gen0Collections}/{sample.Gen1Collections}/{sample.Gen2Collections}"));
        }

        var reportPath = WriteJsonReport();
        context.WriteLine($"JSON report: {reportPath}");
    }

    private string WriteJsonReport()
    {
        var root = PerformanceTestGate.GetString("GRAPH_DATA_PERF_OUTPUT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "GraphDataPerformanceResults");
        Directory.CreateDirectory(root);

        var fileName = string.Join(
            "-",
            "graphdata",
            Scenario,
            StorageKind,
            $"nodes{Graph.NodeCount}",
            $"edges{Graph.Edges.Count}",
            $"seed{Graph.Seed}",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)) + ".json";

        var path = Path.Combine(root, fileName);
        var report = new PerformanceReport(
            Scenario,
            StorageKind,
            Graph.NodeCount,
            Graph.ConnectionsPerNode,
            Graph.Edges.Count,
            Graph.Seed,
            Graph.ContainsCycle,
            StorageRootPath,
            Environment.ProcessorCount,
            Environment.MachineName,
            Environment.Version.ToString(),
            _startedAtUtc,
            _samples);

        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
    }

    private static string Format(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "-";
    }
}

internal sealed record PerformanceReport(
    string Scenario,
    string StorageKind,
    int NodeCount,
    int ConnectionsPerNode,
    int EdgeCount,
    int Seed,
    bool ContainsCycle,
    string StorageRootPath,
    int ProcessorCount,
    string MachineName,
    string RuntimeVersion,
    DateTimeOffset StartedAtUtc,
    IReadOnlyCollection<PerformanceSample> Measurements);

internal sealed record PerformanceSample(
    string Operation,
    int Count,
    double ElapsedMilliseconds,
    double? AverageMicrosecondsPerOperation,
    double CpuMilliseconds,
    double CpuPercent,
    double AllocatedMegabytes,
    double ManagedMemoryDeltaMegabytes,
    double WorkingSetDeltaMegabytes,
    double PrivateMemoryDeltaMegabytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double? MinMicroseconds,
    double? P50Microseconds,
    double? P95Microseconds,
    double? MaxMicroseconds)
{
    public static PerformanceSample Create(
        string operation,
        int count,
        TimeSpan elapsed,
        ResourceSnapshot start,
        ResourceSnapshot finish,
        IReadOnlyCollection<TimeSpan>? itemDurations)
    {
        var elapsedMilliseconds = elapsed.TotalMilliseconds;
        var cpuMilliseconds = (finish.TotalProcessorTime - start.TotalProcessorTime).TotalMilliseconds;
        var elapsedForCpu = elapsed.TotalSeconds <= 0 ? 0 : elapsed.TotalSeconds * Environment.ProcessorCount;
        var cpuPercent = elapsedForCpu <= 0 ? 0 : (finish.TotalProcessorTime - start.TotalProcessorTime).TotalSeconds / elapsedForCpu * 100;
        var sortedDurations = itemDurations?
            .Select(static duration => duration.TotalMilliseconds * 1000)
            .Order()
            .ToArray();

        return new PerformanceSample(
            operation,
            count,
            elapsedMilliseconds,
            count <= 0 ? null : elapsed.TotalMilliseconds * 1000 / count,
            cpuMilliseconds,
            cpuPercent,
            ToMegabytes(finish.TotalAllocatedBytes - start.TotalAllocatedBytes),
            ToMegabytes(finish.ManagedMemoryBytes - start.ManagedMemoryBytes),
            ToMegabytes(finish.WorkingSetBytes - start.WorkingSetBytes),
            ToMegabytes(finish.PrivateMemoryBytes - start.PrivateMemoryBytes),
            finish.Gen0Collections - start.Gen0Collections,
            finish.Gen1Collections - start.Gen1Collections,
            finish.Gen2Collections - start.Gen2Collections,
            sortedDurations is { Length: > 0 } ? sortedDurations[0] : null,
            Percentile(sortedDurations, 50),
            Percentile(sortedDurations, 95),
            sortedDurations is { Length: > 0 } ? sortedDurations[^1] : null);
    }

    private static double? Percentile(double[]? sortedValues, int percentile)
    {
        if (sortedValues is null || sortedValues.Length == 0)
        {
            return null;
        }

        var rank = percentile / 100.0 * (sortedValues.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = rank - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * weight;
    }

    private static double ToMegabytes(long bytes)
    {
        return bytes / 1024d / 1024d;
    }
}

internal sealed record ResourceSnapshot(
    TimeSpan TotalProcessorTime,
    long TotalAllocatedBytes,
    long ManagedMemoryBytes,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    public static ResourceSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new ResourceSnapshot(
            process.TotalProcessorTime,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }
}
