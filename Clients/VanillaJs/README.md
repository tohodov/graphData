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
