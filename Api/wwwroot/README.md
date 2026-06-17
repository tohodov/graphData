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

## Lazy graph navigation

The viewer treats the graph as a lazily loaded neighborhood rather than a fully
materialized database snapshot. Loading a node from the backend also returns its
incident edges. Each edge response carries enough endpoint metadata to draw the
relation immediately, even when the node on the other end has not been loaded as
a full node expansion yet.

Because of that, the UI can show a frontier of known-but-not-expanded
connections. Edges expose small endpoint controls in the graph overlay:

- Collapse controls are shown on both ends of an expanded edge. Clicking either
  endpoint collapses that connection from the local expansion tree and removes
  the branch that was opened through it.
- Expand controls are shown for collapsed/frontier endpoints. Clicking them can
  simply re-expand a collapsed connection, or it can trigger a backend request to
  load the neighbor node when that endpoint is known only from edge metadata.

This lets users walk the graph incrementally: every expanded node reveals the
next ring of edge endpoints, and each endpoint button is both a navigation affordance
and the boundary where additional graph data may be requested.

The graph canvas uses a shared renderer interface. WebGPU is the default renderer,
and SVG is available as both a fallback and an explicit mode via
`/?renderer=svg`. The domain model, projection basis, rank logic, and API access
stay in the TypeScript application code; render buffers are derived caches shared
by both renderer implementations.

Browsers expose WebGPU only in a secure context: `https://...`, `localhost`, or
`127.0.0.1`. Opening the UI through an unsafe `http://<network-ip>` address will
fall back to SVG rendering.
