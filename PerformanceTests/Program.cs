using System;
using System.Reflection;
using System.Threading.Tasks;

namespace GraphData.Tests.Performance;

internal static class Program {
    public static async Task<int> Main() {
        return await PerformanceScenarioRunner.RunAllAsync(
            Assembly.GetExecutingAssembly(),
            Console.Out,
            Console.Error).ConfigureAwait(false);
    }
}
