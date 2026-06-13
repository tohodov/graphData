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

WebGPU is the only active graph rendering layer. The SVG renderer is currently
disabled so WebGPU initialization failures stay visible instead of being hidden
by a slow fallback. The domain model, projection basis, rank logic, and API
access stay in the TypeScript application code; render buffers are derived
caches.

Browsers expose WebGPU only in a secure context: `https://...`, `localhost`, or
`127.0.0.1`. Opening the UI through an unsafe `http://<network-ip>` address will
show the WebGPU error instead of falling back to another renderer.
