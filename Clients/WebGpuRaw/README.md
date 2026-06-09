# WebGpuRaw client

Статический экспериментальный клиент GraphData без frontend framework, npm-сборки и стороннего graph renderer.

Цель клиента - проверить нижнюю границу подхода "сырой WebGPU + плотные массивы":

- загрузка подграфа через существующий `POST /api/graph/subgraph`;
- хранение горячего пути в `Float32Array` и `Uint32Array`;
- отдельные GPU buffers для узлов и ребер;
- отрисовка узлов instanced quads через WebGPU;
- отрисовка ребер батчем `line-list`;
- выбор узла через CPU scan по текущим экранным координатам;
- DOM обновляется только для счетчиков и выбранного узла.

Этот клиент не реализует tile pyramid и semantic zoom. Он нужен как raw baseline для сравнения с будущими клиентами.

## Запуск

API может отдавать этот клиент как корневой static UI:

```json
{
  "WebClient": {
    "ClientsRootPath": "../Clients",
    "RootPath": "../Clients/WebGpuRaw"
  }
}
```

Все клиенты также доступны через общий static mount:

- `/clients/VanillaJs/`
- `/clients/WebGpuRaw/`
