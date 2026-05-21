# Performance tests

Тесты из этой папки отключены для обычного `dotnet test`: без явного флага они завершаются как skipped/inconclusive.

Минимальный запуск:

```powershell
$env:GRAPH_DATA_PERF_TESTS = '1'
dotnet test Tests\Tests.csproj --filter TestCategory=Performance
```

Полезные параметры:

- `GRAPH_DATA_PERF_STORAGES` - список хранилищ через запятую: `PerNodeFile`, `BucketedFile`, `SymLink`.
- `GRAPH_DATA_PERF_NODE_COUNT` - число вершин детерминированно генерируемого графа.
- `GRAPH_DATA_PERF_CONNECTIONS_PER_NODE` - целевое число ребер на вершину.
- `GRAPH_DATA_PERF_SEED` - seed генератора ребер и выборок.
- `GRAPH_DATA_PERF_SAMPLE_COUNT` - размер выборки для `get`, `update`, `get-connected`.
- `GRAPH_DATA_PERF_PARALLELISM` - число параллельных рабочих задач в нагрузочном чтении.
- `GRAPH_DATA_PERF_LOAD_OPERATIONS` - число операций в нагрузочном чтении.
- `GRAPH_DATA_PERF_OUTPUT_DIR` - директория для JSON-отчетов. По умолчанию используется `%TEMP%\GraphDataPerformanceResults`.

Пример сравнимого запуска двух хранилищ:

```powershell
$env:GRAPH_DATA_PERF_TESTS = '1'
$env:GRAPH_DATA_PERF_STORAGES = 'PerNodeFile,BucketedFile'
$env:GRAPH_DATA_PERF_NODE_COUNT = '1000'
$env:GRAPH_DATA_PERF_CONNECTIONS_PER_NODE = '4'
$env:GRAPH_DATA_PERF_SEED = '1729'
dotnet test Tests\Tests.csproj --filter TestCategory=Performance
```

Отчеты содержат wall-clock время, среднее время операции, min/p50/p95/max для поштучно измеряемых операций, CPU time процесса, примерную CPU utilization, allocated bytes, managed memory, working set, private memory и счетчики GC. Для строгого профилирования CPU/памяти лучше запускать эти же тесты под `dotnet-counters`, `dotnet-trace`, PerfView или Visual Studio Profiler: встроенные счетчики удобны для сравнения прогонов, но зависят от фоновой нагрузки ОС и поведения GC.
