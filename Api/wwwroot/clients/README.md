# GraphData clients

Static web clients served by the same `Api` project.

The `Api` project serves this directory from `Api/wwwroot/clients`.
The application root redirects to `/clients/TilePyramid/`.

## Clients

- `VanillaJs` - current extracted baseline UI.
- `WebGpuRaw` - framework-free WebGPU prototype with typed-array graph memory.
- `TilePyramid` - map-like semantic zoom prototype with client-side tile levels.

To change the default client, update `DefaultClientPath` in `Api/Program.cs`.
