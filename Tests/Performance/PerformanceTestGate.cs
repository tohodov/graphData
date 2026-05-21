using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Performance;

internal static class PerformanceTestGate
{
    private const string EnableVariableName = "GRAPH_DATA_PERF_TESTS";

    public static void EnsureEnabled(PerformanceStorageKind storageKind)
    {
        if (!IsEnabled())
        {
            Assert.Inconclusive(
                $"Performance tests are disabled by default. Set {EnableVariableName}=1 to run them.");
        }

        var enabledStorages = GetStorageFilter();
        if (enabledStorages.Count > 0 && !enabledStorages.Contains(storageKind.ToString()))
        {
            Assert.Inconclusive(
                $"Storage '{storageKind}' is filtered out by GRAPH_DATA_PERF_STORAGES.");
        }
    }

    public static int GetInt(string variableName, int defaultValue, int minValue = 1)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, out var parsed)
            ? Math.Max(minValue, parsed)
            : defaultValue;
    }

    public static string? GetString(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string GetStorageBaseRoot()
    {
        var configured = GetString("GRAPH_DATA_PERF_STORAGE_ROOT");
        if (configured is not null)
        {
            return Path.GetFullPath(configured);
        }

        const string preferredRoot = @"E:\TTT";
        if (Directory.Exists(preferredRoot))
        {
            return preferredRoot;
        }

        return Path.Combine(Path.GetTempPath(), "GraphDataPerformanceStorage");
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableVariableName)
            ?? Environment.GetEnvironmentVariable("GRAPHDATA_PERF_TESTS");

        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<string> GetStorageFilter()
    {
        var value = Environment.GetEnvironmentVariable("GRAPH_DATA_PERF_STORAGES");
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
