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

- `Abstractions` - минимальные контракты и backing-состояния графа (`IGraphStorage`, `NodeBacking`, `EdgeBacking`, id-типы, `ServiceResult`). Реализации storage зависят только от этого уровня и не знают доменные `Node`/`Edge`.
- `Domain` - доменная модель (`Graph`, `Node`, `Edge`, типы графовых элементов, query/search/subgraph-модели) и фасад `GraphService`, который выполняет async-операции над открытым графом.
- `GraphCompiler` - downstream-компилятор конечного типизированного snapshot в канонические dense/CSR tensors, message-passing execution plan и HLSL-артефакт. CPU reference executor служит oracle для GPU backend'ов; подробности находятся в `Compiler/README.md`.

Исполняемые входы (`Api`, `Mcp`) должны работать через `GraphService`, а не через `IGraphStorage`. Storage-проект `SymLinkStorage` остается ниже домена и ссылается только на `Abstractions`.
Storage-state типы и storage-контракты закрыты как `internal`; доступ к ним выдается только `Domain`, storage-проектам и тестовым сборкам через `InternalsVisibleTo`.

### Attributes policy

`NodeBacking.Attributes` - нетипизированный escape hatch и временный костыль для прототипирования, импорта внешних
данных, отображаемых подписей и короткоживущих UI-экспериментов. Атрибуты не должны становиться источником
доменной логики, инвариантов, feature branching или типовой семантики графа.

Новые фичи не должны принимать решения по строковым атрибутам вроде `graph.kind`, `graph.role`, `graph.typeName`
или аналогичным convention-ключам. Если поведение требует знать, что узел является типом, инстансом, портом,
слотом, endpoint'ом или частью typed edge subgraph, это должно быть выражено связями графа, типизированной
доменной моделью, DSL-описанием типа и проверкой инвариантов в `Domain`/`GraphService`.

Техническая граница закреплена тестом `AttributePolicyTests`: код в `Domain` не должен читать или ветвиться по
`NodeBacking.Attributes` вне явно разрешенных зон. Разрешения сейчас только такие: `Node` пробрасывает атрибуты как
пользовательские данные, а `GraphSearchService` ищет по ним по прямому запросу пользователя. В `Domain` не должно
быть внутренних `*AttributeNames*`-констант: внутренние состояния, включая marker'ы и display-настройки, должны
выражаться узлами, связями или внешним UI-слоем, а не ключами в attributes.

### Domain DSL: фактический переходный срез

Текущий код подробнее описан в `Domain/README.md`. Сейчас реализован первый сквозной, но еще переходный срез:

- `Graph` открывает storage и материализует зарегистрированные C# `NodeType` в дефолтном каталоге `NodeTypes`.
- `NodeTypeDefinition`, `NodeFieldDefinition`, `NodeTypeBuilder`, `NodeSlotDefinition` и `NodeSlotCardinality` описывают локальный C# DSL одного типа.
- `GraphService.AssignNodeTypeAsync` материализует отдельный typed `InstanceOf` subgraph для каждого effective type. Прямая raw-связь `node — type` пока сохраняется лишь как legacy/UI compatibility index.
- `requires` хранится обычным typed-edge subgraph. Замыкание материализуется с `visited`, схлопывает diamond и завершается на циклах.
- `GetSemanticNodeAsync` строит runtime `InstanceNode` с несколькими materialized `NodeTypeInstance` и принимает явный basis вне дефолтного каталога.
- `TypedEdgeSubgraphCodec` централизует переходную raw-грамматику typed edges: смысл endpoint задаёт отдельный member classifier, а raw-узел его конкретного occurrence имеет техническое уникальное имя. `GetTypedEdgeInstanceAsync` восстанавливает typed edge без fixed-depth parsing.
- HTTP API сохраняет прежний raw-контракт. `GetSemanticNodeAsync` используют Domain и semantic-профиль MCP; он принимает явный basis, а при его отсутствии использует дефолтный каталог. Semantic MCP намеренно не публикует raw depth/search как будто это семантические операции. Web UI остаётся raw-клиентом и строит свою существующую локальную проекцию по выбранному basis; `NodeTypes` остаётся обычным top-level raw-узлом и подчиняется общим правилам basis.

Этот срез сохраняется ради совместимости API, MCP и активных сущностей `Node`/`Edge`; он не фиксирует финальную метамодель.

### Согласованное видение семантического уровня

Сырой граф — carrier-уровень из storage nodes и одинаковых неориентированных raw-ребер. Для двух различных raw-узлов допустимо ровно одно ребро: parent/child и junction — две физические формы одного и того же отношения, а не два независимых ребра. Повторный `Connect` и коллизия имени raw junction должны быть ошибкой хранилища, а не создавать алиас. Семантический граф не хранится как еще один слой persisted-сущностей. Он возникает в runtime как ленивая проекция:

```text
semantic projection = P(available raw graph fragment, selected basis)
```

Базис — это выбранный набор storage nodes, которые в данной проекции считаются типами и запускают интерпретацию типизованных подграфов. Один raw-граф может иметь разные корректные проекции при разных базисах.

У semantic node или edge нет обязательного persisted semantic ID, выделенного raw-root или эксклюзивной storage-границы. Их идентичность внутри проекции — это идентичность runtime-объекта C# или JS. Существующие raw `GlobalId`/`NodeRef` служат locator'ами: по ним runtime-объект ныряет в carrier-граф, дедуплицирует обход и лениво догружает связанные фрагменты. В storage может быть много top-level roots и независимых компонент; semantic projection не вводит для них общий semantic root.

Каждый эффективный тип semantic object материализуется в carrier-графе отдельным type-instance/facet. Для пары `(semantic object, type)` ожидается не более одного type-instance. `requires` означает, что type-instance производного типа требует физического наличия type-instances всех его базовых типов.

Наследование имеет set/virtual-семантику:

- diamond материализует общий базовый type-instance ровно один раз, независимо от числа путей;
- циклы `requires` разрешены как конечные взаимосвязанные подграфы;
- замыкание строится обычным обходом с `visited` по raw locator, без ограничения фиксированной глубиной;
- C++ non-virtual bases, автоматический MRO, override и слияние полей по имени не входят в первую версию.

Типизированное semantic edge — не особый метакласс, а тот же класс semantic object. Оно так же имеет материализованные type-instances, а его связность задаётся endpoint/member instances. Смысл endpoint задаёт конкретный endpoint/member type (classifier) в объявившем типе; raw-узел occurrence лишь уникально представляет одно участие и через raw locator ссылается на semantic object. Поэтому имена raw occurrence не являются частью доменной идентичности. Так различаются `Flight.origin` и `Flight.destination`, даже если оба target имеют тип `City`. Бинарная связь и гиперребро отличаются кардинальностью endpoint instances, а не разным storage-контрактом.

Типы, `requires`, endpoint/member types и их инстансы хранятся в том же carrier-графе. Метауровень может замкнуться конечным циклическим подграфом; для этого не нужна бесконечная лестница метатипов или отдельный hardcoded `GraphKernel`. Базис даёт начальную runtime-интерпретацию, а дальше проекция применяет одинаковые правила к прикладным данным и самому type graph.

Целевая проекция выглядит так:

```text
carrier graph
  raw nodes + undirected raw edges
        |
        | распознавание semantic-object subgraphs и их type instances
        v
semantic graph
  typed nodes + typed edges/hyperedges
        |
        | те же механизмы применены к объявлениям типов
        v
type graph
  node types + edge types + endpoint/member types + requires/includes
```

Проекция строится лениво: чтение типов, member values или endpoints догружает только тот raw-фрагмент, который нужен конкретному вопросу. Фиксированная глубина остаётся допустимой для явного raw-поиска и UI-навигации, но не является семантикой `implements`, `requires`, `is assignable` или чтения endpoint/member instance.

Если при чтении вместо одного ожидаемого type-instance, classifier, endpoint или member value найдено ноль или несколько, первая версия бросает обычное исключение. Специальная иерархия semantic errors и автоматический repair пока не нужны.

#### Дальнейший долг

- Довести переходную raw-грамматику до однородного самоописания type-instance, typed edge, endpoint/member instance и `requires`; пока classifier relation-node еще представлен прямой raw-связью.
- Расширить сквозную проекцию с materialized node types и typed endpoints на member values, primitive values и произвольные semantic edge types.
- Расширить Domain и semantic MCP reader с semantic nodes на semantic edges и members, не меняя raw API.
- Заменить валидацию именованных полей по «любому соседу подходящего типа» на чтение конкретного member instance.
- Определить, как отличать типы, явно назначенные пользователем, от type-instances, материализованных из `requires`, если это понадобится для удаления или редактирования.

## Web UI lazy navigation

Web UI открывается сразу с обзором корневых узлов. Это намеренно не полная загрузка графа,
а стартовая frontier-точка для пошагового просмотра.

Все node-shaped payload'ы, которые использует UI, должны возвращать узел вместе с его incident `edges`. Благодаря этому UI видит
связи к еще не загруженным соседям и рисует кнопки `+` на концах ребер. Нажатие на такую кнопку вызывает
ленивую догрузку соседа.

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
конкретного узла. Для связей строка показывает endpoints, тип, rank и typed edge node. Типизированные связи
редактируются через id своего промежуточного узла как через обычный узел-инстанс связи; физические связи без
typed edge node остаются read-only по атрибутам, но могут быть типизированы операцией смены типа связи.

Правая вкладка `Операции` не дублирует инспектор выбранного узла. В ней остаются только действия над
текущим выбором и графом: снять выбор, удалить выбранные узлы, назначить тип выбранным узлам, назначить тип
выбранным связям, создать узел и соединить ровно один выбранный узел с целью. Операции, которые
неприменимы к текущему выбору, должны оставаться видимыми, но disabled.

Смена типа связи выполняется не как локальное изменение UI и не как простая подмена атрибута. Это должна быть
доменная операция `GraphService`, которая принимает endpoints базовой связи или id существующего typed edge node,
строит typed edge subgraph по DSL выбранного типа и возвращает `Subgraph` созданного подграфа. Для базовой
физической связи операция заменяет прямую связь на typed edge subgraph. Для уже типизированной связи старый
промежуточный узел разбирается и создается новый, потому что разные типы связей могут требовать разный набор
связанных узлов и разные внутренние инварианты.

## Tests

В репозитории есть четыре основные тестовые сборки и отдельное консольное приложение для замеров:

- `DomainTests` - проверка доменных сервисов и активных DDD-сущностей `Node`/`Edge`.
- `ApiTests` - проверка HTTP API и UI, который живет внутри проекта `Api`.
- `McpTests` - проверка раздельных raw/semantic MCP-поверхностей и типизированных mutation-сценариев.
- `GraphCompilerTests` - проверка канонического CSR IR, CPU reference execution, raw lowering и GPU source artifacts.
- `PerformanceTests` - консольный раннер измерительных сценариев производительности.

Unit-тестовой сборки в проекте намеренно нет: корректность должна подтверждаться функциональными тестами, которые проверяют поведение через реальные сервисы и хранилища.

Доменные тесты разделяются по смыслу, а не считаются одной «спецификацией всего»:

- `GraphStorageContractTests`, `SymLinkGraphStorageTests`, backup/search и raw `GetSubgraph` — carrier/storage regressions. Они не определяют semantic typing.
- `SemanticLayerTests` — исполняемая спецификация текущего semantic-среза: несколько типов, materialized witnesses, diamond, циклический `requires`, reopen, явный basis и ошибки неоднозначности.
- typed-edge round-trip тест в `ServiceTests` проверяет доменное чтение endpoint'ов; проверки конкретных папок рядом с ним являются временной regression-защитой carrier codec, а не финальной метамоделью.
- старые sync-тесты `DslTests` проверяют активные `Node`/`Edge` и legacy slot API. Slot-сценарий «любой сосед подходящего типа» явно переходный и не должен использоваться как аргумент против member instances.
- API frontier/UI lazy tests и raw MCP tests сохраняют контракты фронтендов; `SemanticLayerTests` и semantic MCP tests отдельно проверяют проекцию, не меняя raw DTO.

```powershell
dotnet test
```

Для выборочного запуска MSTest по измененным C# API подключен сабмодуль
`TestImpactOnCoverage ([origin](https://github.com/tohodov/TestImpactOnCoverage))`.
`DomainTests` и `ApiTests` помечены `[RelevantTestClass]`; при включенном `RelevantTestsEnabled`
MSBuild target генерирует `RelevantTests.plan.json`, копирует его в output тестовой сборки,
а нерелевантные методы завершаются ранним `TestResult` без выполнения тела. `PerformanceTests` в этот сценарий не входит.

По умолчанию артефакты попадают в `artifacts/test-impact/<timestamp>`.
Если менялись сами тесты, `TestSupport`, UI-файлы в `Api/wwwroot`, `.csproj` или инфраструктура решения,
скрипт запускает соответствующую тестовую сборку целиком, потому что Roslyn-анализатор отслеживает только C# API.
Обычный `dotnet test` без `RelevantTestsEnabled=true` остается полным прогоном.
