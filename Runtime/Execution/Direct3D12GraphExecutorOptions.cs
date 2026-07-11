namespace GraphData.Compiler.Runtime.Execution;

public sealed class Direct3D12GraphExecutorOptions
{
    /// <summary>Optional path to dxc.exe; DXC_PATH and then PATH are used by default.</summary>
    public string? DxcExecutablePath { get; init; }

    /// <summary>
    /// Uses the Direct3D WARP adapter when no hardware D3D12 adapter is available.
    /// Disabled by default so callers do not mistake software execution for GPU execution.
    /// </summary>
    public bool AllowWarpFallback { get; init; }
}
