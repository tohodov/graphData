# Performance tests

Тесты из этой сборки отключены для обычного `dotnet test`: без явного флага они завершаются как skipped/inconclusive.

Минимальный запуск:

```powershell
$env:GRAPH_DATA_PERF_TESTS = '1'
dotnet test PerformanceTests\PerformanceTests.csproj
```

Полезные параметры:

- `GRAPH_DATA_PERF_STORAGE_ROOT` - базовая директория рабочих данных хранилищ. По умолчанию используется `E:\TTT`, если эта папка существует, иначе `%TEMP%\GraphDataPerformanceStorage`.
- `GRAPH_DATA_PERF_NODE_COUNT` - число вершин детерминированно генерируемого графа.
- `GRAPH_DATA_PERF_CONNECTIONS_PER_NODE` - целевое число ребер на вершину.
- `GRAPH_DATA_PERF_SEED` - seed генератора ребер и выборок.
- `GRAPH_DATA_PERF_SAMPLE_COUNT` - размер выборки для `get`, `update`, `get-connected`.
- `GRAPH_DATA_PERF_PARALLELISM` - число параллельных рабочих задач в нагрузочном чтении.
- `GRAPH_DATA_PERF_LOAD_OPERATIONS` - число операций в нагрузочном чтении.
- `GRAPH_DATA_PERF_SUBGRAPH_SAMPLE_COUNT` - число детерминированных single-root и multi-root запросов подграфа.
- `GRAPH_DATA_PERF_SUBGRAPH_DEPTHS` - глубины подграфа через запятую, например `1,2,3`.
- `GRAPH_DATA_PERF_SUBGRAPH_ROOT_COUNT` - число roots в multi-root запросах подграфа.

Пример сравнимого запуска двух хранилищ:

```powershell
$env:GRAPH_DATA_PERF_TESTS = '1'
$env:GRAPH_DATA_PERF_STORAGE_ROOT = 'E:\TTT'
$env:GRAPH_DATA_PERF_NODE_COUNT = '1000'
$env:GRAPH_DATA_PERF_CONNECTIONS_PER_NODE = '10'
$env:GRAPH_DATA_PERF_SEED = '1729'
dotnet test PerformanceTests\PerformanceTests.csproj
```

Рабочие данные каждого сценария размещаются в отдельном подкаталоге вида `<root>\GraphDataPerformanceTests\<run-id>\<scenario>\<storage>`, поэтому разные хранилища не пишут в одну папку, а следующие запуски с автоматически сгенерированным `run-id` не затирают предыдущие. Тесты намеренно не удаляют эти данные после завершения, чтобы их можно было исследовать вручную. Генератор создает не дерево: при `nodeCount >= 3` и `connectionsPerNode > 0` базовый граф содержит цикл, а затем дополняется детерминированными ребрами по seed. Сценарий `subgraph-random-reads` измеряет серии `GetSubgraphAsync` для детерминированно выбранных single-root и multi-root запросов, включая время операции и размер возвращенного подграфа.

Отчеты содержат wall-clock время, среднее время операции, min/p50/p95/max для поштучно измеряемых операций, CPU time процесса, примерную CPU utilization, allocated bytes, managed memory, working set, private memory и счетчики GC. Для строгого профилирования CPU/памяти лучше запускать эти же тесты под `dotnet-counters`, `dotnet-trace`, PerfView или Visual Studio Profiler: встроенные счетчики удобны для сравнения прогонов, но зависят от фоновой нагрузки ОС и поведения GC.
