using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Performance;

internal static class PerformanceTestGate {
    private const string EnableVariableName = "GRAPH_DATA_PERF_TESTS";
    public static string RunId { get; } = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-f}";

    public static void EnsureEnabled(PerformanceStorageKind storageKind) {
        if (!IsEnabled())
            Assert.Inconclusive($"Performance tests are disabled by default. Set {EnableVariableName}=1 to run them.");
    }

    public static int GetInt(string variableName, int defaultValue, int minValue = 1) {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return int.TryParse(value, out var parsed)
            ? Math.Max(minValue, parsed)
            : defaultValue;
    }

    public static IReadOnlyList<int> GetIntList(string variableName, IReadOnlyList<int> defaultValues, int minValue = 0) {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value)) {
            return defaultValues;
        }

        var parsed = value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? Math.Max(minValue, number) : (int?)null)
            .Where(static number => number.HasValue)
            .Select(static number => number!.Value)
            .Distinct()
            .Order()
            .ToArray();

        return parsed.Length == 0 ? defaultValues : parsed;
    }

    public static string? GetString(string variableName) {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string GetStorageBaseRoot() {
        var configured = GetString("GRAPH_DATA_PERF_STORAGE_ROOT");
        if (configured is not null) {
            return Path.GetFullPath(configured);
        }

        const string preferredRoot = @"E:\TTT";
        if (Directory.Exists(preferredRoot)) {
            return preferredRoot;
        }

        return Path.Combine(Path.GetTempPath(), "GraphDataPerformanceStorage");
    }

    public static string SanitizePathSegment(string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray();

        return new string(chars);
    }

    private static bool IsEnabled() {
        var value = Environment.GetEnvironmentVariable(EnableVariableName) ?? Environment.GetEnvironmentVariable("GRAPHDATA_PERF_TESTS");
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
