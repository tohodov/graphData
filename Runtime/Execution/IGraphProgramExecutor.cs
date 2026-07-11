using GraphData.Compiler.Ir;
using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Runtime.Execution;

public interface IGraphProgramExecutor : IDisposable
{
    DenseTensor Execute(CompiledMessagePassingProgram program, CancellationToken cancellationToken = default);
}
