# Domain

## Назначение

`Domain` предоставляет активные `Node`/`Edge`, локальный DSL типов и фасад `GraphService` над каноническим семантическим хранилищем.

Storage уже знает минимальную семантическую форму элемента:

- `Type` находится в отдельном древовидном каталоге;
- `Instance` хранит слабые `TypeRef` рядом со своими данными;
- `Relation` хранит тип и все именованные роли с участниками в одном документе.

Domain отвечает за интерпретацию type definitions, effective type closure, cardinality, прикладную валидацию и совместимые runtime-объекты. Он не должен восстанавливать смысл из формы реифицированного carrier-подграфа.

## Типы

`Graph` при открытии получает фиксированный `TypeRoot` из `ISemanticGraphStorage` и материализует зарегистрированные C# graph types в каталоге `NodeTypes`.

`NodeTypeDefinition`, `NodeFieldDefinition`, `NodeTypeBuilder`, `NodeSlotDefinition` и `NodeSlotCardinality` описывают один локальный тип. Dynamic definitions хранятся в локальном поддереве `Definition/Fields/Slots`; `requiredTypeIds` записываются в metadata типа.

У инстанса различаются:

- `DeclaredTypeIds` — идентификаторы, физически записанные в его документе, включая dangling weak refs;
- `DeclaredTypes` — доступные сейчас типы, в которые эти ссылки разрешились;
- `AssignedTypes` — разрешённые типы плюс транзитивное замыкание `requiredTypeIds`.

Замыкание имеет set-семантику: diamond схлопывается, циклы завершаются через `visited`. Изменение требований типа не переписывает все его инстансы.

## Отношения

`GraphService.CreateRelationAsync` создаёт canonical relation record. Каждая роль содержит:

- имя в рамках типа отношения;
- слабый `MemberTypeId`;
- один или несколько `ParticipantIds` согласно cardinality.

Отдельные persisted carrier/member nodes не создаются. Бинарные, n-арные, параллельные и higher-order отношения имеют один формат. Higher-order означает, что участником может быть другая relation record.

Зависимости между higher-order отношениями должны образовывать DAG. При create/replace storage локально обходит DFS-замыкание отношений, указанных участниками изменяемой записи, и отклоняет прямую или транзитивную ссылку обратно на неё; полный scan instance-графа для этой проверки не нужен.

`TypedEdgeSubgraphCodec` сохраняет историческое имя, но теперь является преобразователем между `TypedEdgeDefinition`/`TypedEdgeInstance` и canonical relation records. Его relation/member nodes существуют только как compatibility wrappers для старых потребителей.

Поиск инцидентных отношений выполняется через `ISemanticGraphStorage.FindIncidentRelationsAsync`, то есть через локальный производный индекс участника, а не обход carrier-графа.

## Instance graph и адреса

Инстансы образуют граф только через relation records. `GlobalId` может содержать несколько сегментов, но префикс пути — пространство имён, не semantic parent edge.

Следствия:

- `a/b` не считается автоматически связанным с `a`;
- можно явно создать отношение между `a` и `a/b`;
- удаление `a` не удаляет `a/b`;
- descendant search по пути остаётся запросом к пространству имён, а не обходом instance graph.

## Compatibility projection

Часть существующего API ещё принимает `IGraphStorage`. Файловое хранилище вычисляет для неё read-only представление:

- type tree даёт навигацию по каталогу;
- `TypeRef` может показываться как виртуальная связь к типу;
- typed relation проецируется в relation/member/participant форму;
- нетипизированная бинарная relation record проецируется прямым ребром.

Эта проекция не записывается на диск. Новая доменная логика не должна определять тип или роль по её глубине, имени виртуального узла или набору raw-соседей.

## Публичные границы

- API и MCP вызывают `GraphService`, а не мутируют storage напрямую.
- `GetNode` даёт общую форму с `SemanticKind`, `DeclaredTypeIds` и `RelationMembers`.
- `GetSemanticNodeAsync` разрешает доступные type refs и считает effective closure; изменяемого basis-аргумента нет.
- Raw subgraph/search остаются диагностическими и навигационными операциями; их depth не определяет типовую семантику.
- `Attributes` являются пользовательскими данными и display metadata, но не discriminator'ом semantic kind.

## Backup и удаление

`GraphBackupService` version 2 перечисляет canonical semantic elements. В backup не попадают виртуальные relation members, compatibility edges и incidence indexes.

При восстановлении сначала создаются типы и обычные инстансы, затем отношения по мере доступности их участников. Слабые type refs могут оставаться dangling.

Удаление инстанса поручено storage: удаляется точная запись и непосредственно инцидентные отношения. Целостность производного индекса также принадлежит storage.

## Ограничения эксперимента

- Dynamic definition всё ещё представлена локальным поддеревом, а не одним вложенным JSON object.
- Для node-valued полей и slots пока не определён отдельный канонический assignment record. Поэтому обязательный такой member отклоняет `AssignNodeTypeAsync`, необязательный отсутствующий member считается пустым, а raw-соседи никогда не используются для угадывания имени поля. Semantic MCP также отклоняет переданные node-valued fields до создания инстанса. Связь следует моделировать явным Edge type и создавать через `CreateRelation`.
- `NodeTypeInstance.Witness` сохранён для API compatibility, но новые type assignments не создают materialized witness nodes.
- Специальной иерархии repair/conflict events пока нет; ошибки локальных контрактов возвращаются обычным `ServiceResult` или исключением storage.

Физическая схема и критерий оценки гипотезы находятся в [../docs/semantic-storage.md](../docs/semantic-storage.md).
