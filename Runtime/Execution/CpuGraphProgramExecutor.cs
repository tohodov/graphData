using GraphData.Compiler.Execution;
using GraphData.Compiler.Ir;
using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Runtime.Execution;

/// <summary>
/// Test and diagnostic executor. Production callers should use the Direct3D12 executor.
/// </summary>
public sealed class CpuGraphProgramExecutor : IGraphProgramExecutor
{
    private readonly CpuMessagePassingExecutor executor = new();

    public DenseTensor Execute(CompiledMessagePassingProgram program, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return executor.Execute(program);
    }

    public void Dispose()
    {
    }
}
