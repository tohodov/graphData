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

## Tests

В репозитории есть две отдельные тестовые сборки:

- `FunctionalTests` - основная проверка поведения через функциональные сценарии.
- `PerformanceTests` - измерительные сценарии производительности, отключенные по умолчанию без `GRAPH_DATA_PERF_TESTS=1`.

Unit-тестовой сборки в проекте намеренно нет: корректность должна подтверждаться функциональными тестами, которые проверяют поведение через реальные сервисы и хранилища.

```powershell
dotnet test FunctionalTests\FunctionalTests.csproj

$env:GRAPH_DATA_PERF_TESTS = '1'
dotnet test PerformanceTests\PerformanceTests.csproj
```

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
