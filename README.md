# graphData

graphData - исследовательский прототип графовой псевдо базы данных.

"Псевдо" здесь означает, что проект не пытается сразу реализовать стандартный набор возможностей баз данных. Сейчас ценность проекта задается экспериментом с моделью хранения и связности графа.

Приоритеты развития:

1. Correctness of experiment
2. Architecture
3. Code quality
4. Tests
5. Docs

## MCP server

В solution добавлен локальный MCP-сервер `Mcp` со stdio-транспортом. Его можно запускать из корня репозитория:

```powershell
dotnet run --project Mcp/Mcp.csproj
```

Для VS Code/Copilot уже добавлен workspace-конфиг `.vscode/mcp.json`. Клиенту доступны tools:

- `get_node`
- `create_node`
- `update_node_attributes`
- `connect_nodes`
- `get_subgraph`
