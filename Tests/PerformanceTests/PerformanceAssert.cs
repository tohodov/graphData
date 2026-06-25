using System;
using System.Collections.Generic;

namespace GraphData.Tests.Performance;

internal static class PerformanceAssert {
    public static void IsTrue(bool condition, string? message = null) {
        if (!condition) {
            throw new PerformanceAssertionException(message ?? "Expected condition to be true.");
        }
    }

    public static void IsNotNull(object? value, string? message = null) {
        if (value is null) {
            throw new PerformanceAssertionException(message ?? "Expected value to be non-null.");
        }
    }

    public static void AreEqual<T>(T expected, T actual, string? message = null) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new PerformanceAssertionException(
                message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void StartsWith(string value, string expectedPrefix, string? message = null) {
        if (!value.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) {
            throw new PerformanceAssertionException(
                message ?? $"Expected <{value}> to start with <{expectedPrefix}>.");
        }
    }

    public static void Contains(string value, string expectedSubstring, string? message = null) {
        if (!value.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase)) {
            throw new PerformanceAssertionException(
                message ?? $"Expected <{value}> to contain <{expectedSubstring}>.");
        }
    }
}
