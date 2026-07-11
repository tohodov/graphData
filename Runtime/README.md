# GraphCompiler.Runtime

`GraphCompiler.Runtime` исполняет скомпилированный graphData snapshot на Direct3D 12. Он зависит от `Domain`, но не от storage implementation: входом служит публичный `GraphService` или уже полученный `Subgraph`.

```text
GraphService.GetSubgraph
    -> RawSubgraphSnapshotAdapter
    -> TypedGraphChunker
    -> GraphProgramCompiler + HLSL emitter
    -> dxc.exe -> DXIL
    -> Direct3D 12 dispatch/readback
    -> node outputs by GlobalId
```

## Запуск

```csharp
using var executor = new Direct3D12GraphProgramExecutor();
using var runtime = new DomainGraphGpuRuntime(executor, ownsExecutor: true);

var result = await runtime.ExecuteAsync(
    graphService,
    roots,
    maxDepth: 2,
    featureSelector: node => [/* float features */],
    specification: messagePassingSpec,
    chunkingOptions: new GraphChunkingOptions { MaxCoreNodes = 65_536 });
```

`result.NodeOutputs` возвращает `float[]` для каждого raw `GlobalId` в выбранном подграфе.

## Chunks и halo

Чанк владеет набором **core** target-узлов. Для каждой входящей relation в них runtime добавляет source-узел как read-only **halo**. Поэтому результат каждого core-узла вычисляется ровно один раз, даже если его source находится в соседнем чанке.

Это является разбиением одного message-passing слоя. Многослойная сеть требует передавать результаты слоя в features следующего слоя и заново строить либо переиспользовать snapshot.

## Требования

- Windows с hardware Direct3D 12 adapter;
- `dxc.exe` в `PATH` либо путь в `DXC_PATH`/`Direct3D12GraphExecutorOptions.DxcExecutablePath`;
- NuGet bindings `Vortice.Direct3D12` и `Vortice.DXGI` версии `3.8.3`.

WARP можно включить явно через `AllowWarpFallback`; по умолчанию он выключен, чтобы software device не выдавался за GPU.

## Границы первого runtime-среза

- поддерживается один статический directed message-passing layer из `GraphCompiler`;
- Domain frontend сейчас lower'ит raw carrier graph: каждое неориентированное ребро становится двумя relation instances;
- semantic typed edges, гиперрёбра и basis-dependent projection требуют отдельного frontend;
- один target с halo больше ограничений dispatch нельзя автоматически разделить без partial reduction;
- после постановки GPU dispatch отмена ожидает fence, чтобы не освобождать ресурсы пока GPU их читает.
