# GraphData clients

Static web clients served by the same `Api` project.

The `Api` project serves this directory from `Api/wwwroot/clients`.
The application root redirects to `/clients/VanillaJs/`.

## Clients

- `VanillaJs` - primary TypeScript UI with the domain model, graph projection, and ranking logic.
- `WebGpuRaw` - framework-free WebGPU prototype with typed-array graph memory.

To change the default client, update `DefaultClientPath` in `Api/Program.cs`.
