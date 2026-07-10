# Domain

## Назначение

`Domain` отвечает за типизированный смысл графа. `IGraphStorage` остается низкоуровневым контрактом carrier-графа: он хранит узлы, одинаковые неориентированные raw-связи, пути и атрибуты, но не знает, что такое тип, type-instance, member или semantic edge. Для одной пары различных узлов carrier допускает только одно неориентированное ребро: существующая hierarchy-связь и junction не образуют параллельных ребер, а повторное соединение — ошибка. Целевое видение не требует расширять storage-контракт.

Типовая семантика выражается самим графом и доменными runtime-объектами, а не строковыми attributes и не snapshot-схемой всего условно бесконечного графа.

## Фактически реализованный переходный срез

Этот раздел описывает текущий сквозной срез, а не финальную метамодель.

- `Graph` открывает storage и материализует зарегистрированные C#-типы в дефолтном каталоге `NodeTypes`.
- `GraphSchemaRegistry` и `NodeTypeFieldDiscovery` читают public C# fields/properties классов `NodeType`. `NodeTypeDefinition`, `NodeFieldDefinition`, `NodeTypeBuilder`, `NodeSlotDefinition` и `NodeSlotCardinality` описывают локальный DSL одного типа.
- Dynamic type definitions пока хранятся в подграфе `Definition/Fields/Slots`; это переходное кодирование локальной дефиниции, а не полный schema snapshot.
- `GraphService.AssignNodeTypeAsync` строит для каждого effective type отдельный typed `InstanceOf` subgraph. Прямая raw-связь `node — type` пока дублируется как legacy/UI compatibility index; semantic read предпочитает materialized witness.
- `RequiresEdge` описывает `requires` обычным typed edge. `GraphService` материализует closure с `visited`, не дублирует общий base в diamond и завершает обход на циклах.
- Slot validation пока считает любых соседей допустимого типа. Два именованных typed-member с пересекающимся target type заранее отклоняются как неоднозначные до появления member instances; plain `Node`-поля и primitive values всё ещё остаются переходным нестрогим срезом.
- `TypedEdgeSubgraphCodec` создаёт и читает переходную raw-грамматику relation node. Каждый endpoint occurrence связан с отдельным member classifier; имя occurrence технически уникально в relation node и не кодирует доменный смысл endpoint. `GetTypedEdgeInstanceAsync` восстанавливает `TypedEdgeInstance` и проверяет endpoint cardinality.
- Обе формы `ChangeEdgeTypeAsync` используют этот codec. При повторной типизации один найденный carrier заменяется, а несколько carrier с теми же участниками считаются неоднозначностью и не изменяются.
- `GetSemanticNodeAsync` строит runtime `InstanceNode` с коллекцией `NodeTypeInstance`, восстанавливает materialized witnesses после повторного открытия `Graph` и принимает явный basis вне `NodeTypes`.
- `Node` и `Edge` остаются важными активными сущностями над backing-состоянием. Raw `GetNode` всегда возвращает обычный `Node`, чтобы carrier API оставался доступен даже при повреждённой семантике. Явный `GetSemanticNodeAsync` возвращает `InstanceNode`; его `InstanceOf` incidences восстанавливаются из прочитанных type-instances.
- Raw `GetSubgraph(maxDepth)` и `GraphSearchService` остаются корректными инструментами исследования carrier-графа. Их depth не должен определять type closure.

## Согласованная runtime-проекция

Semantic layer определяется не самим storage, а парой:

```text
available raw graph fragment + selected basis
```

Базис — выбранный набор storage nodes, которые в текущей проекции считаются типами и запускают интерпретацию. Каталог `NodeTypes` может остаться дефолтным базисом для совместимости, но он не является единственным возможным type root.

### Runtime identity и raw locators

Semantic node/edge не является persisted aggregate. Ему не требуются:

- новый persisted semantic ID;
- один выделенный raw-root;
- эксклюзивное владение raw-фрагментом;
- общий semantic root для всех storage roots и компонент.

В runtime объект имеет обычную reference identity C#/JS. Существующие raw `GlobalId`/`NodeRef` — это locator'ы для перехода в carrier-граф, дедупликации обхода и ленивой догрузки. Они не становятся новой доменной системой semantic IDs.

### Type instances и `requires`

Каждый effective type semantic object должен быть материализован отдельным type-instance/facet в raw-графе. Для пары `(semantic object, type)` ожидается не более одного type-instance.

`requires(derived, base)` означает: если semantic object реализует `derived`, у него физически материализован и type-instance `base`. Множественное наследование имеет set/virtual-семантику:

- общий base в diamond материализуется один раз;
- число путей к типу не влияет на число facets;
- циклы типов образуют конечные взаимосвязанные подграфы;
- closure читается обычным обходом с `visited` по raw locator, а не по фиксированной глубине.

В первой версии нет C++ non-virtual bases, MRO, override и автоматического слияния одноимённых fields. Состояние типа живёт в его собственном facet.

### Typed semantic edges и members

Semantic edge — тот же класс semantic object, а не особое типовое исключение. Оно имеет те же type-instances и может участвовать в `requires`. Его участники задаются endpoint/member instances:

```text
endpoint instance
  implements a concrete endpoint/member type
  references a participant through its backing raw locator
```

Идентичность member — это конкретный member type в объявившем его типе, а не глобальный `Role`, не raw local id occurrence и не один target type. Поэтому `Flight.origin : City` и `Flight.destination : City` различаются по member classifier. Бинарные связи, направленные связи и гиперребра используют один механизм с разными endpoint types и cardinality.

Type graph хранится в том же carrier-графе: types, edge types, endpoint/member types и `requires` сами являются semantic objects и typed edges. Рекурсия метауровня может замкнуться конечным циклическим подграфом. Начальную интерпретацию даёт basis; отдельный hardcoded `GraphKernel` не предполагается.

### Lazy read и неоднозначность

Проекция ленива: semantic node догружает types, members или incident semantic edges только по запросу. Она не строит «полный semantic subgraph» и не загружает всю связную компоненту.

Если по локальному контракту ожидается ровно один type-instance, classifier, endpoint или member value, а найдено ноль или несколько, первая реализация бросает обычное исключение. Специальная иерархия semantic exceptions и автоматический repair добавятся только при реальной необходимости.

## Границы публичных контрактов

- Storage API и raw-поведение `Create/Get/Connect/Disconnect/GetSubgraph/Search` не меняются ради semantic projection.
- `GraphService` остаётся async-границей доменных операций для API и MCP.
- Активные сущности `Node` и `Edge` остаются важным публичным способом работы с `Domain`; они и `GraphService` должны давать одну и ту же semantic interpretation.
- Выбор basis является входом runtime-проекции, а не глобальной мутацией storage.
- `NodeBacking.Attributes` остаются raw user data/escape hatch и не являются источником типовой логики.

## Чего не добавлять

- Persisted semantic ID поверх raw `NodeRef` без конкретной необходимости.
- Обязательный root/ownership boundary для semantic object.
- `NodeTypeSchema` или другой snapshot всех типов графа.
- Публичный словарь hardcoded graph IDs/`GraphKernel`, подменяющий basis.
- Универсальный `Role`, если тот же смысл выражается конкретным member/endpoint type.
- Fixed-depth raw traversal как ответ на вопрос о типе или member value.

## Дальнейший долг

- Довести переходную raw-грамматику до однородного самоописания type-instance, typed semantic edge, endpoint/member instance и `requires`; classifier relation-node пока еще задаётся прямой raw-связью.
- Расширить проекцию с type-instances и typed endpoints на конкретные member occurrences, primitive values и произвольные semantic edge types.
- Заменить плоскую slot-валидацию на чтение конкретных member instances.
- Передавать явный basis и в semantic mutations/cleanup; сейчас basis вне дефолтного каталога поддержан reader'ами, а назначение типов и каскадная очистка работают по зарегистрированному `NodeTypes`.
- Удалить оставшиеся `PortNodeType` и UI-ветви legacy source/target после окончательного перехода сохранённых старых графов на `TypedEdgeSubgraphCodec`.
- Расширить Domain и semantic MCP reader с node type-instances на semantic edges и members, сохранив raw API и ленивую frontier-загрузку UI.
- Отдельно решить, нужно ли хранить происхождение direct и materialized-through-`requires` types для удаления и редактирования.
