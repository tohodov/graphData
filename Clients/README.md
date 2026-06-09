# GraphData clients

Static web clients served by the same `Api` project.

The selected root client is configured by `WebClient:RootPath`. All clients can be served under `/clients/*` when `WebClient:ClientsRootPath` points to this directory.

## Clients

- `VanillaJs` - current extracted baseline UI.
- `WebGpuRaw` - framework-free WebGPU prototype with typed-array graph memory.
- `TilePyramid` - map-like semantic zoom prototype with client-side tile levels.

## Switching root client

```json
{
  "WebClient": {
    "ClientsRootPath": "../Clients",
    "RootPath": "../Clients/TilePyramid"
  }
}
```
