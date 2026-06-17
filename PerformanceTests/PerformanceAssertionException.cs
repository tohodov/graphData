using System;

namespace GraphData.Tests.Performance;

internal sealed class PerformanceAssertionException : Exception {
    public PerformanceAssertionException(string message)
        : base(message) {
    }

    public PerformanceAssertionException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
