using System.Runtime.InteropServices;
using GraphData.Compiler.Backends.Hlsl;
using GraphData.Compiler.Ir;
using GraphData.Compiler.Runtime.Shaders;
using GraphData.Compiler.Tensors;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D3D12Api = Vortice.Direct3D12.D3D12;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace GraphData.Compiler.Runtime.Execution;

/// <summary>
/// Executes a compiled message-passing program on a D3D12 adapter.
/// The public contract remains backend-neutral; this class consumes the HLSL
/// artifact and performs the DXIL, buffer, dispatch and readback steps.
/// </summary>
public sealed class Direct3D12GraphProgramExecutor : IGraphProgramExecutor
{
    private const int ShaderResourceCount = 8;
    private const int UnorderedAccessCount = 1;
    private readonly object executionGate = new();
    private readonly ID3D12Device device;
    private readonly ID3D12CommandQueue queue;
    private readonly ID3D12Fence fence;
    private readonly IDXGIAdapter1 adapter;
    private readonly DxcProcessShaderCompiler shaderCompiler;
    private ulong nextFenceValue = 1;
    private bool disposed;

    public string AdapterDescription { get; }

    public Direct3D12GraphProgramExecutor(Direct3D12GraphExecutorOptions? options = null)
    {
        options ??= new Direct3D12GraphExecutorOptions();
        (adapter, device) = CreateDevice(options.AllowWarpFallback);
        AdapterDescription = adapter.Description1.Description.TrimEnd('\0');
        queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        fence = device.CreateFence(0);
        shaderCompiler = new DxcProcessShaderCompiler(options.DxcExecutablePath);
    }

    public DenseTensor Execute(CompiledMessagePassingProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        if (program.Plan.NodeCount == 0)
            return new DenseTensor([0, program.Plan.OutputWidth], []);

        lock (executionGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = new HlslMessagePassingEmitter().Emit(program);
            var shaderBytecode = shaderCompiler.Compile(artifact, cancellationToken);
            var output = ExecuteArtifact(artifact, shaderBytecode, cancellationToken);
            return new DenseTensor([program.Plan.NodeCount, program.Plan.OutputWidth], output);
        }
    }

    private float[] ExecuteArtifact(
        HlslMessagePassingArtifact artifact,
        byte[] shaderBytecode,
        CancellationToken cancellationToken)
    {
        using var rootSignature = CreateRootSignature();
        using var pipelineState = device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = rootSignature,
            ComputeShader = shaderBytecode
        });
        using var descriptorHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            ShaderResourceCount + UnorderedAccessCount,
            DescriptorHeapFlags.ShaderVisible));
        using var commandAllocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var commandList = device.CreateCommandList<ID3D12GraphicsCommandList>(
            0,
            CommandListType.Direct,
            commandAllocator,
            pipelineState);

        var inputData = CreateInputBuffers(artifact.Buffers);
        using var outputResource = CreateDefaultBuffer(
            checked((ulong)artifact.BufferMetadata.Single(static buffer => buffer.Name == "Output").AllocationSizeInBytes),
            ResourceFlags.AllowUnorderedAccess,
            ResourceStates.UnorderedAccess);
        using var readbackResource = device.CreateCommittedResource(
            new HeapProperties(HeapType.Readback),
            HeapFlags.None,
            ResourceDescription.Buffer(checked((ulong)artifact.BufferMetadata.Single(static buffer => buffer.Name == "Output").AllocationSizeInBytes)),
            ResourceStates.CopyDest);

        try
        {
            UploadInputs(commandList, inputData);
            CreateDescriptors(descriptorHeap, inputData, outputResource, artifact.BufferMetadata);
            commandList.ResourceBarrier(inputData.Select(static buffer => ResourceBarrier.BarrierTransition(
                buffer.Default,
                ResourceStates.CopyDest,
                ResourceStates.NonPixelShaderResource)).ToArray());
            commandList.SetPipelineState(pipelineState);
            commandList.SetComputeRootSignature(rootSignature);
            commandList.SetDescriptorHeaps(descriptorHeap);
            commandList.SetComputeRoot32BitConstants(0, Constants(artifact.Constants), 0);
            commandList.SetComputeRootDescriptorTable(1, descriptorHeap.GetGPUDescriptorHandleForHeapStart());
            commandList.SetComputeRootDescriptorTable(
                2,
                descriptorHeap.GetGPUDescriptorHandleForHeapStart().Offset(ShaderResourceCount,
                    device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView)));
            commandList.Dispatch(
                checked((uint)artifact.Dispatch.GroupCountX),
                checked((uint)artifact.Dispatch.GroupCountY),
                checked((uint)artifact.Dispatch.GroupCountZ));
            commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
                outputResource,
                ResourceStates.UnorderedAccess,
                ResourceStates.CopySource));
            commandList.CopyBufferRegion(readbackResource, 0, outputResource, 0,
                checked((ulong)artifact.BufferMetadata.Single(static buffer => buffer.Name == "Output").AllocationSizeInBytes));
            commandList.Close();
            queue.ExecuteCommandList(commandList);
            WaitForCompletion(cancellationToken);

            return ReadFloatBuffer(readbackResource, artifact.Constants.NodeCount, artifact.Constants.OutputWidth);
        }
        finally
        {
            foreach (var buffer in inputData)
                buffer.Dispose();
        }
    }

    private ID3D12RootSignature CreateRootSignature()
    {
        var parameters = new[]
        {
            new RootParameter(new RootConstants(0, 0, HlslMessagePassingConstants.SizeInBytes / sizeof(uint)), ShaderVisibility.All),
            new RootParameter(new RootDescriptorTable([
                new DescriptorRange(DescriptorRangeType.ShaderResourceView, ShaderResourceCount, 0)
            ]), ShaderVisibility.All),
            new RootParameter(new RootDescriptorTable([
                new DescriptorRange(DescriptorRangeType.UnorderedAccessView, UnorderedAccessCount, 0)
            ]), ShaderVisibility.All)
        };
        var description = new RootSignatureDescription(RootSignatureFlags.None, parameters);
        return device.CreateRootSignature(in description, RootSignatureVersion.Version1);
    }

    private InputBuffer[] CreateInputBuffers(HlslPackedMessagePassingBuffers buffers)
    {
        return
        [
            CreateInputBuffer(buffers.NodeFeatures.ToArray()),
            CreateInputBuffer(buffers.RowOffsets.ToArray()),
            CreateInputBuffer(buffers.ColumnIndices.ToArray()),
            CreateInputBuffer(buffers.EdgeWeights.ToArray()),
            CreateInputBuffer(buffers.RelationTransforms.ToArray()),
            CreateInputBuffer(buffers.RelationDescriptors.ToArray()),
            CreateInputBuffer(buffers.SelfTransform.ToArray()),
            CreateInputBuffer(buffers.Bias.ToArray())
        ];
    }

    private InputBuffer CreateInputBuffer<T>(T[] values) where T : struct
    {
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var upload = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer(checked((ulong)bytes.Length)),
            ResourceStates.GenericRead);
        var mapped = upload.Map<byte>(0, bytes.Length);
        try
        {
            bytes.CopyTo(mapped);
        }
        finally
        {
            upload.Unmap(0);
        }

        var defaultBuffer = CreateDefaultBuffer(
            checked((ulong)bytes.Length),
            ResourceFlags.None,
            ResourceStates.CopyDest);
        return new InputBuffer(defaultBuffer, upload, bytes.Length);
    }

    private ID3D12Resource CreateDefaultBuffer(ulong size, ResourceFlags flags, ResourceStates initialState) =>
        device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Buffer(size, flags),
            initialState);

    private static void UploadInputs(ID3D12GraphicsCommandList commandList, IEnumerable<InputBuffer> buffers)
    {
        foreach (var buffer in buffers)
            commandList.CopyBufferRegion(buffer.Default, 0, buffer.Upload, 0, checked((ulong)buffer.SizeInBytes));
    }

    private void CreateDescriptors(
        ID3D12DescriptorHeap descriptorHeap,
        IReadOnlyList<InputBuffer> inputs,
        ID3D12Resource output,
        IReadOnlyList<HlslBufferMetadata> metadata)
    {
        var increment = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        foreach (var buffer in metadata.Where(static buffer => buffer.ResourceKind == HlslBufferResourceKind.ShaderResource))
        {
            var resource = inputs[buffer.Register].Default;
            var description = new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = 0,
                    NumElements = checked((uint)buffer.AllocationElementCount),
                    StructureByteStride = checked((uint)buffer.StrideInBytes),
                    Flags = BufferShaderResourceViewFlags.None
                }
            };
            device.CreateShaderResourceView(resource, description,
                descriptorHeap.GetCPUDescriptorHandleForHeapStart().Offset(buffer.Register, increment));
        }

        var outputMetadata = metadata.Single(static buffer => buffer.ResourceKind == HlslBufferResourceKind.UnorderedAccess);
        var outputDescription = new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = checked((uint)outputMetadata.AllocationElementCount),
                StructureByteStride = checked((uint)outputMetadata.StrideInBytes),
                CounterOffsetInBytes = 0,
                Flags = BufferUnorderedAccessViewFlags.None
            }
        };
        device.CreateUnorderedAccessView(output, null, outputDescription,
            descriptorHeap.GetCPUDescriptorHandleForHeapStart().Offset(ShaderResourceCount, increment));
    }

    private void WaitForCompletion(CancellationToken cancellationToken)
    {
        var value = nextFenceValue++;
        queue.Signal(fence, value).CheckError();
        if (fence.CompletedValue >= value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        fence.SetEventOnCompletion(value, waitHandle.SafeWaitHandle.DangerousGetHandle()).CheckError();
        waitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static float[] ReadFloatBuffer(ID3D12Resource readback, uint nodeCount, uint outputWidth)
    {
        var elementCount = checked((int)(nodeCount * outputWidth));
        var values = new float[elementCount];
        var mapped = readback.Map<float>(0, values.Length);
        try
        {
            mapped.CopyTo(values);
        }
        finally
        {
            readback.Unmap(0);
        }
        return values;
    }

    private static uint[] Constants(HlslMessagePassingConstants constants) =>
    [
        constants.NodeCount,
        constants.InputWidth,
        constants.OutputWidth,
        constants.RelationCount,
        constants.HasSelfTransform,
        constants.HasBias,
        constants.Activation,
        constants.Reserved
    ];

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device) CreateDevice(bool allowWarpFallback)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; ; index++)
        {
            var result = factory.EnumAdapters1(index, out IDXGIAdapter1 adapter);
            if (result.Failure)
                break;

            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
                continue;
            }

            var deviceResult = D3D12Api.D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out ID3D12Device? device);
            if (deviceResult.Success && device is not null)
                return (adapter, device);
            adapter.Dispose();
        }

        if (allowWarpFallback)
        {
            using var factory4 = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            factory4.EnumWarpAdapter(out IDXGIAdapter1? warpAdapter).CheckError();
            if (warpAdapter is null)
                throw new InvalidOperationException("DXGI did not return the WARP adapter.");
            D3D12Api.D3D12CreateDevice(warpAdapter, FeatureLevel.Level_12_0, out ID3D12Device? warpDevice).CheckError();
            if (warpDevice is null)
            {
                warpAdapter.Dispose();
                throw new InvalidOperationException("Direct3D 12 did not create the WARP device.");
            }
            return (warpAdapter, warpDevice);
        }

        throw new InvalidOperationException("No hardware Direct3D 12 adapter is available.");
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(Direct3D12GraphProgramExecutor));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        fence.Dispose();
        queue.Dispose();
        device.Dispose();
        adapter.Dispose();
    }

    private sealed class InputBuffer : IDisposable
    {
        public ID3D12Resource Default { get; }
        public ID3D12Resource Upload { get; }
        public int SizeInBytes { get; }

        public InputBuffer(ID3D12Resource @default, ID3D12Resource upload, int sizeInBytes)
        {
            Default = @default;
            Upload = upload;
            SizeInBytes = sizeInBytes;
        }

        public void Dispose()
        {
            Default.Dispose();
            Upload.Dispose();
        }
    }
}
