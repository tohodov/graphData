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
connection immediately, even when the node on the other end has not been loaded as
a full node expansion yet.

The initial no-query screen uses `POST /api/graph/subgraph` with an empty
`globalIds` array and `maxDepth: 0` to load top-level roots. In that response,
`nodes[]` must still include each root node's incident `edges`, including edges
to children or neighbors that are not part of the returned `nodes[]` set. The
top-level `response.edges` collection is only the de-duplicated set of edges
whose two endpoints are both already in `nodes[]`; it does not replace
`node.edges`.

Every node-shaped API response used by the UI follows the same rule: single-node
loads, neighbor loads, create-node responses, subgraph `nodes[]`, search
`node`, and search `bindings` all carry `edges`. The UI uses those per-node
edges as the frontier for lazy expansion.

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
SVG is available as both a fallback and an explicit mode via `/?renderer=svg`,
and the experimental HTML-in-Canvas renderer can be selected with
`/?renderer=html-canvas` or from the toolbar dropdown. The domain model,
projection basis, rank logic, and API access stay in the TypeScript application
code; render buffers are derived caches shared by all renderer implementations.

Browsers expose WebGPU only in a secure context: `https://...`, `localhost`, or
`127.0.0.1`. Opening the UI through an unsafe `http://<network-ip>` address will
fall back to SVG rendering.

HTML-in-Canvas follows the WICG `drawElementImage` proposal and currently requires
Chromium with `chrome://flags/#canvas-draw-element` enabled. When the API is not
available, the renderer dropdown keeps the option visible but disables it after
the one-time availability check performed during page startup.
