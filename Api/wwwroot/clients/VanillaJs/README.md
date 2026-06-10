# VanillaJs client

Static baseline UI for GraphData, served by the `Api` project from
`Api/wwwroot/clients/VanillaJs`.

The client has no separate backend-for-frontend. It talks directly to the same
HTTP endpoints as the other clients:

- `GET /api/graph/nodes`
- `POST /api/graph/nodes`
- `PUT /api/graph/nodes`
- `DELETE /api/graph/nodes`
- `POST /api/graph/connections`
- `POST /api/graph/subgraph`
- `POST /api/graph/search/nodes`

Open it at `/clients/VanillaJs/`.

This folder is kept as the baseline client. Add comparison clients next to it
under `Api/wwwroot/clients/*` without changing the graph API.
