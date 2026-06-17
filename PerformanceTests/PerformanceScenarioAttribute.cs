using System;
using System.Collections.Generic;

namespace GraphData.Tests.Performance;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class PerformanceScenarioAttribute : Attribute {
    public PerformanceScenarioAttribute(params object[] arguments) {
        Arguments = arguments;
    }

    public IReadOnlyList<object> Arguments { get; }
}
