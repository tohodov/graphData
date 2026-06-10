# WebGpuRaw client

Static experimental GraphData client without a frontend framework, npm build,
or third-party graph renderer. It is served by the `Api` project from
`Api/wwwroot/clients/WebGpuRaw`.

The goal is to test a low-level rendering baseline:

- load subgraphs through the existing `POST /api/graph/subgraph` endpoint;
- keep the hot path in `Float32Array` and `Uint32Array`;
- use separate GPU buffers for nodes and edges;
- render nodes as instanced quads through WebGPU;
- render edges in batches with `line-list`;
- pick nodes through a CPU scan in current screen coordinates;
- update DOM only for counters and selected node details.

Open it at `/clients/WebGpuRaw/`.

All static clients live under `Api/wwwroot/clients/*`.
