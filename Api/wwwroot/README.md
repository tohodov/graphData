# GraphData web UI

Static GraphData UI served by the `Api` project from `Api/wwwroot`.

The client has no separate backend-for-frontend. It talks directly to the
existing HTTP endpoints:

- `GET /api/graph/nodes`
- `POST /api/graph/nodes`
- `PUT /api/graph/nodes`
- `DELETE /api/graph/nodes`
- `POST /api/graph/connections`
- `POST /api/graph/subgraph`
- `POST /api/graph/search/nodes`

Open it at `/`.

WebGPU is tried first as the rendering layer. If the browser does not expose
WebGPU, the same canvas falls back to Canvas2D rendering. The domain model,
projection basis, rank logic, and API access stay in the TypeScript application
code; render buffers are derived caches.
