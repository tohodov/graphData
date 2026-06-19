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

### Attributes policy

`NodeState.Attributes` - нетипизированный escape hatch и временный костыль для прототипирования, импорта внешних
данных, отображаемых подписей и короткоживущих UI-экспериментов. Атрибуты не должны становиться источником
доменной логики, инвариантов, feature branching или типовой семантики графа.

Новые фичи не должны принимать решения по строковым атрибутам вроде `graph.kind`, `graph.role`, `graph.typeName`
или аналогичным convention-ключам. Если поведение требует знать, что узел является типом, инстансом, портом,
слотом, endpoint'ом или частью relation-подграфа, это должно быть выражено типизированной доменной моделью,
DSL-описанием типа и проверкой инвариантов в `Domain`/`GraphService`. Существующие места, где runtime-логика
читает такие атрибуты, считаются техническим долгом переходного периода и должны заменяться типострогими
конструкциями перед развитием соответствующих возможностей.

### Type-strict DSL gap

Для реализации типострогого DSL типов узлов, связей и внутренних инвариантов не хватает следующих слоев:

- Явной доменной модели схемы: `NodeType`, `RelationType`/`EdgeType`, `SlotType`, `EndpointSpec`, cardinality,
  направление, допустимые типы endpoint-узлов и правила обязательных внутренних узлов relation-подграфа.
- Типизированных идентификаторов и контрактов вместо строковых convention-ключей: роль порта или слот связи
  должны быть объектами DSL/схемы, а не значениями в `Attributes`.
- Материализатора storage -> domain, который собирает сырой подграф в типизированный объект и возвращает
  диагностируемые ошибки, если подграф не соответствует схеме.
- Валидатора инвариантов на границе `GraphService`: все mutation-операции должны проверять pre/post-conditions
  схемы, cardinality, разрешенные endpoint-типы и полноту relation-подграфа.
- Планировщика операций изменения типа: смена типа связи должна строить новый relation-подграф по DSL,
  переносить совместимые части старого инстанса, явно удалять или запрашивать решение по несовместимым частям,
  а не эвристически переиспользовать узлы.
- Версионирования схем и миграций: типы будут развиваться, поэтому нужен способ понять, по какой версии DSL
  создан инстанс и как безопасно привести его к новой версии.
- API-контрактов, которые говорят терминами домена (`relation type`, `slot`, `endpoint`, `invariant violation`),
  а не произвольными словарями атрибутов.
- UI-метаданных от домена: интерфейс должен получать применимость операций, обязательные поля и ошибки валидации
  из DSL/валидатора, а не повторять правила на клиенте.

Первый реализованный слой DSL относится к типам узлов:

- `NodeTypeDefinition.Define(...)` и `NodeTypeBuilder` задают тип узла и его слоты без строковых runtime-атрибутов.
- `NodeSlotDefinition` и `NodeSlotCardinality` описывают допустимые типы связанных узлов и cardinality слота.
- `NodeTypeSchema` хранит типизированные определения типов узлов.
- `NodeTypeSchemaMaterializer` собирает схему и typed-инстанс из storage-графа: типы узлов определяются положением
  под `GraphSystemNodeIds.NodeTypeRoot`, а назначение типа - связью узла с типовым узлом.
- `NodeTypeValidator` возвращает диагностируемые ошибки инвариантов (`node-type.missing`,
  `node-type.multiple`, `node-type.unknown`, `node-type.abstract`, `node-type.slot-cardinality`).
- `GraphService.AssignNodeTypeAsync` использует materializer и validator перед подтверждением операции назначения
  типа и возвращает `Subgraph` результата через `PUT /api/graph/nodes/type`.

Оставшийся долг: DSL пока не материализует slot-правила из пользовательского графа схемы, а задает их через
доменные определения; DSL связей/relation-подграфов еще не перенесен с `graph.role`/`graph.kind` convention-атрибутов.

## Web UI lazy navigation

Web UI открывается сразу с обзором корневых узлов: клиент вызывает `POST /api/graph/subgraph`
с пустым списком `globalIds` и `maxDepth = 0`. Это намеренно не полная загрузка графа,
а стартовая frontier-точка для пошагового просмотра.

Все API-ответы, которые возвращают узел (`GET /api/graph/nodes`, `GET /api/graph/nodes/{globalId}/neighbor/{localId}`,
`POST /api/graph/nodes`, `POST /api/graph/subgraph` в `nodes[]`, а также `POST /api/graph/search/nodes`
в `node` и `bindings`), должны возвращать узел вместе с его incident `edges`. Благодаря этому UI видит
связи к еще не загруженным соседям и рисует кнопки `+` на концах ребер. Нажатие на такую кнопку вызывает
ленивую догрузку соседа через `/neighbor/{localId}`.

`SubgraphResponse.edges` при этом остается отдельным дедуплицированным списком только тех ребер,
у которых оба endpoint уже входят в `nodes[]`; он нужен для раскладки загруженного подграфа и не заменяет
`NodeResponse.edges`.

## Web UI selection model

Основной сценарий UI - работа с текущим выбором на поле графа. Узлы и связи можно выбирать по одному,
добавлять к выбору через Ctrl/Cmd и выбирать рамкой через Shift-drag. Текущий выбор показывается
левым overlay-списком поверх графа; заголовок списка сворачивает его в компактную кнопку, чтобы не мешать
навигации по полотну.

Каждая строка выбранного элемента является accordion. В свернутом виде строка показывает тип элемента,
человекочитаемое имя и кнопку снятия выделения. В раскрытом виде строка становится инспектором:
для узлов показывает GlobalId, rank, атрибуты и тип, а также позволяет редактировать и сохранять атрибуты
конкретного узла. Для связей строка показывает endpoints, тип, rank и relation-узел. Типизированные связи
редактируются через свой `relationGlobalId` как через обычный узел-инстанс связи; физические связи без
relation-узла остаются read-only по атрибутам, но могут быть типизированы операцией смены типа связи.

Правая вкладка `Операции` не дублирует инспектор выбранного узла. В ней остаются только действия над
текущим выбором и графом: снять выбор, удалить выбранные узлы, назначить тип выбранным узлам, назначить тип
выбранным связям, создать узел и соединить ровно один выбранный узел с целью. Операции, которые
неприменимы к текущему выбору, должны оставаться видимыми, но disabled.

Смена типа связи выполняется не как локальное изменение UI и не как простая подмена атрибута. Это должна быть
доменная операция `GraphService`, которая принимает endpoints базовой связи или существующий `relationGlobalId`,
строит relation-инстанс по DSL выбранного типа и возвращает `Subgraph` созданного relation-подграфа. Для базовой
физической связи операция заменяет прямую связь на relation-подграф. Для уже типизированной связи старый
relation-инстанс разбирается и создается новый, потому что разные типы связей могут требовать разный набор
связанных узлов и разные внутренние инварианты.

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
