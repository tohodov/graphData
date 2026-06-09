# VanillaJs client

Текущий статический web UI GraphData, вынесенный из `Api/wwwroot`.

Клиент не имеет отдельного backend-for-frontend и работает напрямую с теми же HTTP endpoints:

- `GET /api/graph/nodes`
- `POST /api/graph/nodes`
- `PUT /api/graph/nodes`
- `DELETE /api/graph/nodes`
- `POST /api/graph/connections`
- `POST /api/graph/subgraph`
- `POST /api/graph/search/nodes`

API может раздавать эту папку как root static UI через настройку `WebClient:RootPath`.

Также клиент доступен через общий static mount:

- `/clients/VanillaJs/`

Эта папка оставлена как baseline-клиент. Следующие клиенты для сравнения можно добавлять рядом в `Clients/*` и переключать через `WebClient:RootPath`, не меняя graph API.

В типовом базисе клиент вычисляет локальный `viewRank` для загруженной проекции. Основа ранга задается атрибутом `projection.rank` на type-узлах; связи добавляют weighted-degree, выбранный/root узел получают временный boost. Rank не сохраняется на обычных узлах и пока не скрывает элементы автоматически.
