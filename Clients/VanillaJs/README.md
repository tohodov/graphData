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

API раздает эту папку как static files. Путь выбирается настройкой `WebClient:RootPath`; по умолчанию используется `../Clients/VanillaJs`.

Эта папка оставлена как baseline-клиент. Следующие клиенты для сравнения можно добавлять рядом в `Clients/*` и переключать через `WebClient:RootPath`, не меняя API.
