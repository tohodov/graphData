# TilePyramid client

Статический экспериментальный клиент GraphData, вдохновленный подходом Microsoft Research "Browsing Large Graphs with Tile Pyramids and Sleeve Routing in the Browser".

Этот клиент проверяет не WebGPU throughput, а другую идею: граф просматривается как карта.

- подграф загружается через существующий `POST /api/graph/subgraph`;
- layout строится клиентом;
- узлы получают стабильный rank на основе root boost, degree и имени;
- строится несколько уровней тайлов;
- на каждом уровне в тайле выбираются ограниченные наборы nodes, labels и edges;
- labels выбираются по rank и отбрасываются при пересечении bounding boxes;
- при zoom/pan рисуются только видимые тайлы текущего уровня;
- edge routing пока упрощен до clipped/bent edge drawing, без CDT sleeve routing.

## Что это дает для сравнения

`WebGpuRaw` пытается быстро рисовать плотный граф. `TilePyramid` пытается не рисовать лишнее: кадр ограничен текущим zoom level и видимыми тайлами.

Полная версия подхода из статьи потребует:

- предсказуемого layout без пересечения node boxes;
- PageRank или другого доменного ранжирования;
- полноценного edge sleeve routing через triangulation/funnel;
- кэша готовых тайлов и, возможно, worker pipeline.
