# graphData

graphData - исследовательский прототип графовой псевдо базы данных. 

"Псевдо" здесь означает, что проект не пытается сразу реализовать стандартный набор возможностей баз данных. Сейчас ценность проекта задается экспериментом с моделью хранения и связности графа.

Основная цель - динамический mcp rag для нейросетевых агентов.
Побочная цель - база знаний удобная для чтения и редактирования человеком.
Далекая идеализированная цель - ИИ с объянением решения и пошаговой отладкой логики.

Приоритеты развития:

1. Correctness of experiment
2. Architecture
3. Code quality
4. Tests
5. Docs

## Architecture

Основная модель разделена на два нижних уровня:

- `Abstractions` - минимальные контракты и состояния графа (`IGraphStorage`, `NodeState`, `EdgeState`, id-типы, `ServiceResult`). Реализации storage зависят только от этого уровня и не знают доменные `Node`/`Edge`.
- `Domain` - доменная модель (`Node`, `Edge`, типы графовых элементов, query/search/subgraph-модели) и фасад `GraphService`, который преобразует storage-state в доменные объекты.

Исполняемые входы (`Api`, `Mcp`) должны работать через `GraphService`, а не через `IGraphStorage`. Storage-проекты (`SymLinkStorage`, `PerNodeFileStorage`, `BucketedFileStorage`) остаются ниже домена и ссылаются только на `Abstractions`.
Storage-state типы и storage-контракты закрыты как `internal`; доступ к ним выдается только `Domain`, storage-проектам и тестовым сборкам через `InternalsVisibleTo`.

## Tests

В репозитории есть две тестовые сборки и отдельное консольное приложение для замеров:

- `DomainTests` - проверка доменных сервисов и активных DDD-сущностей `Node`/`Edge`.
- `ApiTests` - проверка HTTP API и UI, который живет внутри проекта `Api`.
- `PerformanceTests` - консольный раннер измерительных сценариев производительности.

Unit-тестовой сборки в проекте намеренно нет: корректность должна подтверждаться функциональными тестами, которые проверяют поведение через реальные сервисы и хранилища.

```powershell
dotnet test DomainTests\DomainTests.csproj
dotnet test ApiTests\ApiTests.csproj

dotnet run --project PerformanceTests\PerformanceTests.csproj
```

Для выборочного запуска MSTest по измененным C# API подключен сабмодуль
`TestImpactOnCoverage ([origin](https://github.com/tohodov/TestImpactOnCoverage))`.
`DomainTests` и `ApiTests` помечены `[RelevantTestClass]`; при включенном `RelevantTestsEnabled`
MSBuild target генерирует `RelevantTests.plan.json`, копирует его в output тестовой сборки,
а нерелевантные методы завершаются ранним `TestResult` без выполнения тела. `PerformanceTests` в этот сценарий не входит.

```powershell
git submodule update --init --recursive
powershell -ExecutionPolicy Bypass -File scripts\Run-RelevantTests.ps1 -BaselineRef HEAD
```

По умолчанию артефакты попадают в `artifacts/test-impact/<timestamp>`.
Если менялись сами тесты, `TestSupport`, UI-файлы в `Api/wwwroot`, `.csproj` или инфраструктура решения,
скрипт запускает соответствующую тестовую сборку целиком, потому что Roslyn-анализатор отслеживает только C# API.
Обычный `dotnet test` без `RelevantTestsEnabled=true` остается полным прогоном.

## MCP server

В solution добавлен локальный MCP-сервер `Mcp` со stdio-транспортом. Его можно запускать из корня репозитория:

```powershell
dotnet run --no-launch-profile --project Mcp/Mcp.csproj
```

Для VS Code/Copilot уже добавлен workspace-конфиг `.vscode/mcp.json`. Клиенту доступны tools:

- `get_node`
- `create_node`
- `update_node_attributes`
- `connect_nodes`
- `get_subgraph`

### LM Studio

Для стабильного подключения к LM Studio используйте установленную Release-сборку, а не `dotnet run`.
Скрипт публикует свежие бинарники в `%USERPROFILE%\.lmstudio\graphdata-mcp-server`,
записывает `GraphStorage.RootPath` в `%USERPROFILE%\.lmstudio\graphdata-mcp-server\appsettings.json`
и обновляет `%USERPROFILE%\.lmstudio\mcp.json` без UTF-8 BOM. Вместе с MCP-сервером публикуется
универсальный наблюдатель `Tray.exe` из `McpTracker ([origin](https://github.com/tohodov/McpTracker))`.
Сервер запускает его автоматически, если он еще не запущен, и передает логи через Named Pipe.
Окно логов открывается кликом по значку в области уведомлений; несколько MCP-инстансов и серверов отображаются отдельно.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Install-LmStudioMcp.ps1
```

После запуска скрипта перезапустите LM Studio. В чате сервер должен быть виден как `mcp/graphdata`.
При первом запуске MCP-сервера рядом появится значок `MCP Tracker`.

По умолчанию данные графа хранятся в `graph-data` внутри этого репозитория. Другой путь можно указать так:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Install-LmStudioMcp.ps1 -GraphStorageRoot C:\path\to\graph-data
```

Для отладки реального запуска из LM Studio можно установить сервер в режиме ожидания debugger:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Install-LmStudioMcp.ps1 -DebugWait
```

После этого включите `mcp/graphdata` в LM Studio и подключитесь из Visual Studio к процессу `Mcp.exe`
через `Debug > Attach to Process`. Когда отладка закончена, переустановите обычный режим командой без `-DebugWait`.

### Visual Studio

В `Mcp/Properties/launchSettings.json` есть профили `Mcp` и `Mcp - wait for debugger`.
Их удобно использовать для проверки старта, конфигурации и breakpoint'ов в инициализации.
Для отладки tool-вызовов удобнее запускать сервер из LM Studio в режиме `-DebugWait` и attach'иться к `Mcp.exe`.
