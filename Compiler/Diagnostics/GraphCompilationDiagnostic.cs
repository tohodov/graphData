using System.Collections.ObjectModel;

namespace GraphData.Compiler.Diagnostics;

public sealed record GraphCompilationDiagnostic(string Code, string Message);

public sealed class GraphCompilationException : Exception
{
    public IReadOnlyList<GraphCompilationDiagnostic> Diagnostics { get; }

    public GraphCompilationException(IEnumerable<GraphCompilationDiagnostic> diagnostics)
        : this(diagnostics.ToArray())
    {
    }

    private GraphCompilationException(GraphCompilationDiagnostic[] diagnostics)
        : base(CreateMessage(diagnostics))
    {
        if (diagnostics.Length == 0)
            throw new ArgumentException("At least one compilation diagnostic is required.", nameof(diagnostics));
        Diagnostics = new ReadOnlyCollection<GraphCompilationDiagnostic>(diagnostics);
    }

    private static string CreateMessage(IReadOnlyList<GraphCompilationDiagnostic> diagnostics) =>
        diagnostics.Count == 1
            ? diagnostics[0].Message
            : $"Graph compilation failed with {diagnostics.Count} diagnostics: "
                + string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Message));
}
