# GraphCompiler

`GraphCompiler` — первый компилируемый срез между редактируемым графом `graphData` и числовой программой для GPU. Проект не создаёт ещё один persisted-слой и не пытается угадать семантику ленивой graph projection. На вход подаётся конечный явный snapshot, на выходе получаются канонические tensor IR, execution plan и HLSL-артефакт.

## Поддерживаемая семантика

Первая версия компилирует один слой типизированного directed message passing:

```text
Y[v] = activation(
    bias
    + X[v] * Wself
    + sum по типам r и дугам u -> v (weight(u,v) * X[u] * Wr))
```

- узлы имеют стабильный snapshot-local key, набор type keys и `float32` features;
- relation instance имеет собственный key, один type key, source, target и вес;
- каждому использованному relation type соответствует своя dense transform matrix;
- доступны `Identity` и `ReLU`, aggregation первой версии — `Sum`;
- параллельные relation instances сохраняются отдельными элементами и не схлопываются.

`GraphProgramCompiler` сортирует node/relation type keys через `StringComparer.Ordinal`, назначает плотные индексы и строит отдельную CSR-матрицу для каждого relation type. Ориентация CSR является частью контракта:

```text
row    = target node
column = source node
```

Поэтому `A * X` собирает входящие сообщения в target.

## Pipeline

```text
TypedGraphSnapshot
    -> validation diagnostics
    -> canonical node indices + dense feature tensor
    -> CSR adjacency per relation type
    -> MessagePassingExecutionPlan
       |-> CpuMessagePassingExecutor (эталон корректности)
       `-> HLSL artifact (shader + dispatch + packed buffers)
```

CPU executor намеренно имеет прозрачный детерминированный порядок вычисления. Это oracle для будущих GPU runtime backend'ов: результат конкретного backend должен совпадать с ним в пределах выбранного float tolerance.

HLSL backend выдаёт исходник compute shader, размеры dispatch, constants и уже упакованные CSR/transform buffers. Сам `GraphCompiler` остаётся GPU-независимым; Direct3D 12 execution находится в отдельном `GraphCompiler.Runtime` и использует этот artefact без дублирования семантики.

## Пример

```csharp
var snapshot = new TypedGraphSnapshot(
    [
        new TypedNodeSnapshot("a", ["entity"], [2f]),
        new TypedNodeSnapshot("b", ["entity"], [0f])
    ],
    [new TypedRelationSnapshot("a-to-b", "link", "a", "b")]);

var specification = new MessagePassingSpecification(
    InputWidth: 1,
    OutputWidth: 1,
    RelationTransforms:
    [
        new RelationTransform(
            "link",
            new DenseTensor([1, 1], [1f]))
    ]);

var program = new GraphProgramCompiler().Compile(snapshot, specification);
var expected = new CpuMessagePassingExecutor().Execute(program);
```

## Адаптер raw graph

`RawSubgraphSnapshotAdapter` — намеренно отдельный frontend для текущего carrier-графа `Domain`:

- требует один node wrapper на каждый `GlobalId` и детерминированно отклоняет дубликаты;
- дедуплицирует физические связи по паре endpoint `GlobalId`;
- сортирует узлы детерминированно;
- отбрасывает frontier-рёбра, второй endpoint которых не входит в `Subgraph`;
- преобразует каждое неориентированное raw-ребро в две directed relations веса `1`;
- получает features только через явный callback и не выводит направление, тип или вес из `Attributes`.

Это raw lowering, а не semantic projection. Для типизированных semantic edges, гиперрёбер и выбранного basis нужен отдельный frontend, который сформирует явный `TypedGraphSnapshot`.

## Ограничения первой версии

- компилируется один статический message-passing layer, а не произвольный язык правил;
- topology snapshot не атомарен относительно одновременного редактирования storage;
- нет lowering гиперрёбер в incidence matrices;
- нет распознавания dense/block-sparse областей и kernel fusion cost model;
- нет incremental recompilation;
- Direct3D 12 execution требует отдельный Windows runtime и `dxc.exe`; core compiler не должен вводить эту зависимость.

Дополнительные backend'ы могут использовать тот же `CompiledMessagePassingProgram` и проверяться теми же тестовыми векторами против `CpuMessagePassingExecutor`.
