using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GraphData.Tests.Performance;

internal static class PerformanceScenarioRunner {
    public static async Task<int> RunAllAsync(Assembly assembly, TextWriter output, TextWriter error) {
        var scenarios = Discover(assembly).ToArray();
        if (scenarios.Length == 0) {
            output.WriteLine("No performance scenarios were found.");
            return 1;
        }

        output.WriteLine($"Running {scenarios.Length} performance scenario(s).");
        output.WriteLine($"Run id: {PerformanceTestGate.RunId}");
        output.WriteLine($"Storage base root: {PerformanceTestGate.GetStorageBaseRoot()}");

        var failures = new List<ScenarioFailure>();
        for (var index = 0; index < scenarios.Length; index++) {
            var scenario = scenarios[index];
            output.WriteLine();
            output.WriteLine($"[{index + 1}/{scenarios.Length}] {scenario.DisplayName}");

            var stopwatch = Stopwatch.StartNew();
            try {
                await InvokeAsync(scenario).ConfigureAwait(false);
                stopwatch.Stop();
                output.WriteLine($"Passed in {stopwatch.Elapsed}.");
            } catch (Exception ex) {
                stopwatch.Stop();
                var unwrapped = Unwrap(ex);
                failures.Add(new ScenarioFailure(scenario, unwrapped));
                error.WriteLine($"Failed in {stopwatch.Elapsed}: {scenario.DisplayName}");
                error.WriteLine(unwrapped);
            }
        }

        output.WriteLine();
        output.WriteLine(failures.Count == 0
            ? $"All {scenarios.Length} performance scenario(s) passed."
            : $"{failures.Count} of {scenarios.Length} performance scenario(s) failed.");

        return failures.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<PerformanceScenario> Discover(Assembly assembly) {
        return assembly
            .GetTypes()
            .SelectMany(static type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(static method => method
                    .GetCustomAttributes<PerformanceScenarioAttribute>()
                    .Select((attribute, index) => new PerformanceScenario(method, attribute, index))))
            .OrderBy(static scenario => scenario.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(static scenario => scenario.Method.MetadataToken)
            .ThenBy(static scenario => scenario.AttributeIndex);
    }

    private static async Task InvokeAsync(PerformanceScenario scenario) {
        var method = scenario.Method;
        var arguments = scenario.Attribute.Arguments.ToArray();
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Length) {
            throw new InvalidOperationException(
                $"{scenario.DisplayName} expects {parameters.Length} argument(s), but the attribute provides {arguments.Length}.");
        }

        var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
        var result = method.Invoke(target, arguments);

        switch (result) {
            case Task task:
                await task.ConfigureAwait(false);
                break;

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;

            case null when method.ReturnType == typeof(void):
                break;

            default:
                throw new InvalidOperationException(
                    $"{scenario.DisplayName} must return void, Task, or ValueTask.");
        }
    }

    private static Exception Unwrap(Exception exception) {
        return exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
    }

    private sealed record PerformanceScenario(
        MethodInfo Method,
        PerformanceScenarioAttribute Attribute,
        int AttributeIndex) {
        public string DisplayName {
            get {
                var arguments = Attribute.Arguments.Count == 0
                    ? string.Empty
                    : $"({string.Join(", ", Attribute.Arguments)})";

                return $"{Method.DeclaringType?.Name}.{Method.Name}{arguments}";
            }
        }
    }

    private sealed record ScenarioFailure(PerformanceScenario Scenario, Exception Exception);
}
