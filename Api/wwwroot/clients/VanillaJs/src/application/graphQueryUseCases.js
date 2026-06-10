import {
  svgNs,
  nodeRadius,
  endpointOffset,
  defaultBasis,
  graphKindAttribute,
  graphElementAttribute,
  graphRoleAttribute,
  graphTypeNameAttribute,
  projectionVisibleAttribute,
  projectionColorAttribute,
  projectionInfoAttribute,
  projectionDirectedAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute
} from "../domain/graphConstants.js";

export const graphQueryUseCases = {
  async searchNodeMatches(query) {
  const response = await this.apiClient.fetch("/api/graph/search/nodes", {
    method: "POST",
    headers: {
      "Accept": "application/x-ndjson",
      "Content-Type": "application/json"
    },
    body: JSON.stringify(query)
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  const matches = [];
  await this.readNdjsonStream(response, match => matches.push(match));
  return matches;

  },

  setSearchQueryTemplate(name) {
  const currentName = this.state.selectedName || this.rootInput.value.trim() || "node-name";
  const templates = {
    all: {
      return: ["n"],
      where: {
        kind: "node",
        node: this.variableSelector("n")
      },
      limit: 50
    },
    text: {
      return: ["n"],
      where: {
        kind: "text",
        node: this.variableSelector("n"),
        value: "sample"
      },
      limit: 50
    },
    connected: {
      return: ["n"],
      where: {
        kind: "connected",
        left: this.variableSelector("n"),
        right: this.literalSelector(currentName || "node-name")
      },
      limit: 50
    },
    neighborById: {
      return: ["n", "x"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "connected",
            left: this.variableSelector("n"),
            right: this.variableSelector("x")
          },
          {
            kind: "attribute",
            node: this.variableSelector("x"),
            key: "id",
            operator: "equals",
            value: "Y"
          }
        ]
      },
      limit: 50
    },
    neighborConnected: {
      return: ["n", "x", "z"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "connected",
            left: this.variableSelector("n"),
            right: this.variableSelector("x")
          },
          {
            kind: "connected",
            left: this.variableSelector("x"),
            right: this.variableSelector("z")
          }
        ]
      },
      limit: 50
    },
    isolated: {
      return: ["x"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "node",
            node: this.variableSelector("x")
          },
          {
            kind: "not",
            expression: {
              kind: "exists",
              variables: ["y"],
              expression: {
                kind: "connected",
                left: this.variableSelector("x"),
                right: this.variableSelector("y")
              }
            }
          }
        ]
      },
      limit: 50
    }
  };

  this.searchQueryJson.value = JSON.stringify(templates[name] ?? templates.all, null, 2);

  },

  variableSelector(name) {
  return { kind: "var", name };

  },

  literalSelector(name) {
  return { kind: "literal", name };

  },

  async searchNodes() {
  let query;
  try {
    query = JSON.parse(this.searchQueryJson.value.trim());
  } catch (error) {
    this.setStatus(`JSON: ${error.message}`);
    return;
  }

  this.state.searchAbort?.abort();
  const controller = new AbortController();
  this.state.searchAbort = controller;
  this.setSearchStreaming(true);
  this.renderSearchResults([]);

  let count = 0;
  try {
    const response = await this.apiClient.fetch("/api/graph/search/nodes", {
      method: "POST",
      headers: {
        "Accept": "application/x-ndjson",
        "Content-Type": "application/json"
      },
      body: JSON.stringify(query),
      signal: controller.signal
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `HTTP ${response.status}`);
    }

    await this.readNdjsonStream(response, match => {
      count += 1;
      this.appendSearchResult(match);
      this.setStatus(`Найдено решений: ${count}`);
    });

    this.setStatus(`Найдено решений: ${count}`);
  } catch (error) {
    if (error.name === "AbortError") {
      this.setStatus(`Поиск остановлен: ${count}`);
    } else {
      this.setStatus(error.message);
    }
  } finally {
    if (this.state.searchAbort === controller) {
      this.state.searchAbort = null;
      this.setSearchStreaming(false);
    }
  }

  },

  async readNdjsonStream(response, onItem) {
  if (!response.body) {
    this.parseNdjsonLines(await response.text(), onItem);
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";
    this.parseNdjsonLines(lines.join("\n"), onItem);
  }

  buffer += decoder.decode();
  this.parseNdjsonLines(buffer, onItem);

  },

  parseNdjsonLines(text, onItem) {
  text
    .split("\n")
    .map(line => line.trim())
    .filter(Boolean)
    .forEach(line => onItem(JSON.parse(line)));

  },

  setSearchStreaming(value) {
  this.searchSubmitButton.disabled = value;
  this.searchStopButton.disabled = !value;

  },

  async loadSubgraph() {
  const roots = this.parseCsv(this.document.querySelector("#subgraph-roots").value);
  if (roots.length === 0) {
    this.setStatus("Укажите корневые узлы");
    return;
  }

  this.setBusy(true);
  try {
    const response = await this.apiJson("/api/graph/subgraph", {
      method: "POST",
      body: JSON.stringify({
        globalIds: roots.map(root => this.parseGlobalId(root)),
        maxDepth: this.readNumber("#subgraph-depth", 1),
        includeDisconnectedRoots: this.document.querySelector("#subgraph-include-disconnected").checked
      })
    });
    this.loadSubgraphIntoViewer(response, roots);
    this.renderSubgraphResults(response);
    this.renderTypeControls();
    this.setStatus(`Подграф: ${(response.nodes ?? []).length} узлов, ${(response.edges ?? []).length} ребер`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  loadSubgraphIntoViewer(response, roots) {
  this.state.loaded.clear();
  this.state.parentByNode.clear();
  this.state.positions.clear();
  this.state.velocities.clear();
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = (response.edges ?? []).map(edge => this.normalizeEdgeResponse(edge));
  this.state.rootName = roots[0] ?? nodes[0]?.name ?? null;
  this.state.selectedName = this.state.rootName;

  nodes.forEach((node, index) => {
    this.state.loaded.set(node.name, {
      ...node,
      edges: edges.filter(edge => edge.sourceGlobalId === node.name || edge.targetGlobalId === node.name)
    });
    this.seedSubgraphPosition(node.name, index, nodes.length);
  });

  this.render();
  this.renderTypeControls();
  this.runSimulation(40);
  this.fitView();

  },

  mergeSubgraphIntoViewer(response, options = {}) {
  const select = options.select ?? false;
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = (response.edges ?? []).map(edge => this.normalizeEdgeResponse(edge));
  const edgesByNode = new Map();

  edges.forEach(edge => {
    [edge.sourceGlobalId, edge.targetGlobalId].forEach(name => {
      if (!edgesByNode.has(name)) {
        edgesByNode.set(name, []);
      }
      edgesByNode.get(name).push(edge);
    });
  });

  nodes.forEach((node, index) => {
    const expansion = {
      ...node,
      edges: this.mergeEdges(node.edges, edgesByNode.get(node.name) ?? [])
    };
    this.storeNodeExpansion(expansion, options.fromName ?? null, {
      select: select && index === 0
    });
  });
  this.renderTypeControls();

  },

  async loadSubgraphForRoots(roots, maxDepth = 1) {
  return this.apiJson("/api/graph/subgraph", {
    method: "POST",
    body: JSON.stringify({
      globalIds: roots.map(root => this.parseGlobalId(root)),
      maxDepth
    })
  });

  }
};
