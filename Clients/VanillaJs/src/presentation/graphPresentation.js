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

export const graphPresentation = {
  render() {
  const graph = this.buildGraph();
  this.viewport.replaceChildren();
  this.emptyState.classList.toggle("hidden", graph.nodes.length > 0);

  const edgeLayer = this.createSvg("g", { class: "edges" });
  const nodeLayer = this.createSvg("g", { class: "nodes" });
  const buttonLayer = this.createSvg("g", { class: "edge-buttons" });
  this.viewport.append(edgeLayer, nodeLayer, buttonLayer);

  const nodesByName = new Map(graph.nodes.map(node => [node.name, node]));
  graph.edges.forEach(edge => this.renderEdge(edgeLayer, buttonLayer, edge, nodesByName));
  graph.nodes.forEach(node => this.renderNode(nodeLayer, node));
  this.renderInspector(graph);
  this.applyView();

  },

  renderEdge(edgeLayer, buttonLayer, edge, nodesByName) {
  const sourceLoaded = this.state.loaded.has(edge.sourceGlobalId);
  const targetLoaded = this.state.loaded.has(edge.targetGlobalId);
  const source = this.state.positions.get(edge.sourceGlobalId);
  const target = this.state.positions.get(edge.targetGlobalId);

  if (!source || !target || (!sourceLoaded && !targetLoaded)) {
    return;
  }

  if (sourceLoaded && targetLoaded) {
    const sourceButton = this.pointOnCircle(source, target, this.getNodeEndpointOffset(nodesByName.get(edge.sourceGlobalId)));
    const targetButton = this.pointOnCircle(target, source, this.getNodeEndpointOffset(nodesByName.get(edge.targetGlobalId)));
    const line = this.createSvg("line", {
      class: "edge-line",
      x1: sourceButton.x,
      y1: sourceButton.y,
      x2: targetButton.x,
      y2: targetButton.y,
      style: edge.color ? `stroke:${edge.color}` : ""
    });

    edgeLayer.append(line);
    this.renderEdgeLabel(edgeLayer, source, target, edge);
    this.renderEndpointButton(buttonLayer, sourceButton, edge, edge.sourceGlobalId, false);
    this.renderEndpointButton(buttonLayer, targetButton, edge, edge.targetGlobalId, false);
    return;
  }

  const anchorName = sourceLoaded ? edge.sourceGlobalId : edge.targetGlobalId;
  const hiddenName = sourceLoaded ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchor = sourceLoaded ? source : target;
  const hidden = sourceLoaded ? target : source;
  const buttonPoint = this.pointOnCircle(anchor, hidden, this.getNodeEndpointOffset(nodesByName.get(anchorName)));
  this.renderEndpointButton(buttonLayer, buttonPoint, edge, anchorName, true, hiddenName);

  },

  getNodeEndpointOffset(node) {
  return (node?.viewRadius ?? nodeRadius) + 9;

  },

  renderEdgeLabel(layer, source, target, edge) {
  if (!edge.label && !edge.directed) {
    return;
  }

  const text = this.createSvg("text", {
    class: "edge-label",
    x: (source.x + target.x) / 2,
    y: (source.y + target.y) / 2 - 7,
    style: edge.color ? `fill:${edge.color}` : ""
  });
  text.textContent = `${edge.label ?? ""}${edge.directed ? " ->" : ""}`.trim();
  layer.append(text);

  },

  renderEndpointButton(layer, point, edge, anchorName, collapsed, hiddenName = null) {
  const otherName = hiddenName ?? (edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId);
  const otherDisplayName = this.edgeEndpointDisplayName(edge, otherName);
  const group = this.createSvg("g", {
    class: `edge-button ${collapsed ? "collapsed" : "expanded"}`,
    transform: `translate(${point.x} ${point.y})`,
    role: "button",
    tabindex: "0",
    "aria-label": collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`
  });
  const title = this.createSvg("title", {});
  const titleLines = [collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`];
  if (Number.isFinite(edge.viewRank)) {
    titleLines.push(`Edge rank: ${this.formatRank(edge.viewRank)}`);
  }
  if (edge.viewRankReason) {
    titleLines.push(edge.viewRankReason);
  }
  title.textContent = titleLines.join("\n");

  const hit = this.createSvg("circle", { class: "edge-button-hit", r: 17, cx: 0, cy: 0 });
  const core = this.createSvg("circle", {
    class: "edge-button-core",
    r: collapsed ? 8 : 6,
    cx: 0,
    cy: 0
  });

  const activate = event => {
    event.stopPropagation();
    this.handleEndpointClick(edge, anchorName);
  };

  group.addEventListener("click", activate);
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      activate(event);
    }
  });

  group.append(title, hit, core);
  layer.append(group);

  },

  renderNode(layer, node) {
  const position = this.state.positions.get(node.name);
  if (!position) {
    return;
  }

  const group = this.createSvg("g", {
    class: `node${this.state.selectedName === node.name ? " selected" : ""}`,
    transform: `translate(${position.x} ${position.y})`,
    tabindex: "0",
    "aria-label": node.displayName ?? node.name
  });
  const title = this.createSvg("title", {});
  const titleLines = [node.globalId ?? node.name];
  if (node.typeLabel) {
    titleLines.push(`Type: ${node.typeLabel}`);
  }
  if (Number.isFinite(node.viewRank)) {
    titleLines.push(`Rank: ${this.formatRank(node.viewRank)}`);
  }
  if (node.viewRankReason) {
    titleLines.push(node.viewRankReason);
  }
  title.textContent = titleLines.join("\n");
  const circle = this.createSvg("circle", {
    class: "node-shell",
    r: node.viewRadius ?? nodeRadius,
    cx: 0,
    cy: 0,
    style: node.color ? `stroke:${node.color}` : ""
  });
  const label = this.createSvg("text", { class: "node-label", x: 0, y: 0 });
  label.textContent = this.trimName(node.displayName ?? node.name, 18);

  group.addEventListener("click", event => {
    event.stopPropagation();
    this.state.selectedName = node.name;
    this.render();
  });

  group.addEventListener("pointerdown", event => {
    event.stopPropagation();
    this.svg.setPointerCapture(event.pointerId);
    this.state.dragging = {
      name: node.name,
      x: event.clientX,
      y: event.clientY
    };
  });

  group.append(title, circle, label);
  layer.append(group);

  },

  renderInspector(graph) {
  const selected = graph.nodes.find(node => node.name === this.state.selectedName);
  this.selectedName.textContent = selected?.displayName ?? "-";
  this.selectedName.title = selected?.globalId ?? "";
  this.selectedRank.textContent = selected?.viewRank === undefined ? "rank: -" : `rank: ${this.formatRank(selected.viewRank)}`;
  this.selectedRank.title = selected?.viewRankReason ?? "";
  this.updateEditorState();
  this.renderAttributeEditor(selected?.attributes ?? {});
  this.neighborList.replaceChildren();

  if (!selected) {
    return;
  }

  const neighbors = graph.edges
    .filter(edge => edge.sourceGlobalId === selected.name || edge.targetGlobalId === selected.name)
    .map(edge => edge.sourceGlobalId === selected.name ? edge.targetGlobalId : edge.sourceGlobalId)
    .sort((a, b) => this.displayName(a).localeCompare(this.displayName(b), "ru"));

  neighbors.forEach(name => {
    const edge = graph.edges.find(candidate =>
      (candidate.sourceGlobalId === selected.name && candidate.targetGlobalId === name) ||
      (candidate.sourceGlobalId === name && candidate.targetGlobalId === selected.name));
    const row = this.document.createElement("button");
    row.type = "button";
    row.className = `neighbor-row${this.state.loaded.has(name) ? " loaded" : ""}`;
    const dot = this.document.createElement("span");
    dot.className = "neighbor-dot";
    const text = this.document.createElement("span");
    text.textContent = edge ? this.edgeEndpointDisplayName(edge, name) : this.displayName(name);
    row.title = name;
    row.append(dot, text);
    row.addEventListener("click", () => {
      if (edge) {
        this.handleEndpointClick(edge, selected.name);
      }
    });
    this.neighborList.append(row);
  });

  },

  renderAttributeEditor(attributes) {
  const focused = this.document.activeElement;
  if (focused?.closest("#attribute-editor")) {
    return;
  }

  this.attributeEditor.replaceChildren();
  const entries = Object.entries(attributes);
  if (entries.length === 0) {
    this.addAttributeRow("", "");
    return;
  }

  entries.forEach(([key, value]) => this.addAttributeRow(key, value));

  },

  addAttributeRow(key, value) {
  const row = this.document.createElement("div");
  row.className = "attribute-row";
  const keyInput = this.document.createElement("input");
  keyInput.className = "attribute-key";
  keyInput.placeholder = "Ключ";
  keyInput.value = key;
  const valueInput = this.document.createElement("input");
  valueInput.className = "attribute-value";
  valueInput.placeholder = "Значение";
  valueInput.value = value;
  const removeButton = this.document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "icon-button";
  removeButton.textContent = "×";
  removeButton.addEventListener("click", () => row.remove());
  row.append(keyInput, valueInput, removeButton);
  this.attributeEditor.append(row);

  },

  readAttributeEditor() {
  const attributes = {};
  this.attributeEditor.querySelectorAll(".attribute-row").forEach(row => {
    const key = row.querySelector(".attribute-key").value.trim();
    const value = row.querySelector(".attribute-value").value;
    if (key) {
      attributes[key] = value;
    }
  });
  return attributes;

  },

  renderSearchResults(matches) {
  this.searchResults.replaceChildren();
  matches.forEach(match => this.appendSearchResult(match));

  },

  appendSearchResult(match) {
  const node = this.normalizeNodeResponse(match.node);
  const button = this.document.createElement("button");
  button.type = "button";
  button.className = "result-row";
  button.innerHTML = `<strong></strong><span></span>`;
  button.querySelector("strong").textContent = node.displayName;
  const bindings = Object.entries(match.bindings ?? {})
    .map(([variable, binding]) => {
      const normalized = this.normalizeNodeResponse(binding);
      return `${variable}=${normalized.displayName}`;
    })
    .join(" · ");
  button.querySelector("span").textContent = bindings || `score ${match.score} ${match.matchedBy?.join(" ") ?? ""}`;
  button.title = node.globalId;
  button.addEventListener("click", () => {
    this.rootInput.value = node.globalId;
    this.loadRoot(node.globalId);
    this.setActiveTab("node");
  });
  this.searchResults.append(button);

  },

  renderSubgraphResults(response) {
  this.subgraphResults.replaceChildren();
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = response.edges ?? [];
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${nodes.length} узлов, ${edges.length} ребер`;
  this.subgraphResults.append(summary);
  nodes.forEach(node => {
    const button = this.document.createElement("button");
    button.type = "button";
    button.className = "result-row";
    button.textContent = node.displayName;
    button.title = node.globalId;
    button.addEventListener("click", () => {
      this.state.selectedName = node.name;
      this.setActiveTab("node");
      this.render();
    });
    this.subgraphResults.append(button);
  });

  },

  renderTypeControls() {
  this.renderTypeSelect(this.createNodeType, this.state.schema.nodeTypes, "Без типа");
  this.renderTypeSelect(this.assignNodeType, this.state.schema.nodeTypes, "Не менять тип");
  this.renderTypeSelect(this.connectEdgeType, this.state.schema.edgeTypes, "Физическая связь");
  this.renderTypeList(this.nodeTypeList, "Типы узлов", this.state.schema.nodeTypes, "node");
  this.renderTypeList(this.edgeTypeList, "Типы связей", this.state.schema.edgeTypes, "edge");
  this.renderRelationList();
  this.renderProjectionSummary();

  },

  renderTypeSelect(select, types, emptyLabel = "Выберите тип") {
  const current = select.value;
  select.replaceChildren();
  const empty = this.document.createElement("option");
  empty.value = "";
  empty.textContent = types.size === 0 ? "Типы не загружены" : emptyLabel;
  select.append(empty);
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const option = this.document.createElement("option");
      option.value = type.globalId;
      option.textContent = type.label;
      select.append(option);
    });
  if (types.has(current)) {
    select.value = current;
  }

  },

  renderTypeList(container, title, types, element) {
  container.replaceChildren();
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${title}: ${types.size}`;
  container.append(summary);
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const row = this.document.createElement("div");
      row.className = "basis-rule";
      row.title = type.globalId;

      const header = this.document.createElement("div");
      header.className = "type-row";
      const swatch = this.document.createElement("span");
      swatch.className = "type-swatch";
      swatch.style.background = type.color || "#9daab2";
      const open = this.document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = type.directed ? `${type.label} ->` : type.label;
      open.title = type.globalId;
      open.addEventListener("click", async () => {
        await this.ensureNodeLoaded(type.globalId);
        this.state.selectedName = type.globalId;
        this.render();
        this.setActiveTab("node");
      });
      const save = this.document.createElement("button");
      save.type = "button";
      save.className = "compact-button";
      save.textContent = "Сохранить";
      header.append(swatch, open, save);

      const rules = this.document.createElement("div");
      rules.className = "basis-rule-grid";
      const visible = this.createCheckboxRule("Показывать", type.visible);
      const color = this.createTextRule("Цвет", type.color || "", "#0f766e");
      const rank = this.createTextRule("Ранг", this.formatRankInput(type.rank), element === "node" ? "50" : "30");
      rules.append(visible.label, color.label, rank.label);

      const extraControls = {};
      if (element === "node") {
        extraControls.info = this.createTextRule("Инфо атрибут", type.infoAttribute || "", "например: status");
        rules.append(extraControls.info.label);
      } else {
        extraControls.directed = this.createCheckboxRule("Стрелка", type.directed);
        extraControls.labelVisible = this.createCheckboxRule("Подпись", type.labelVisible);
        rules.append(extraControls.directed.label, extraControls.labelVisible.label);
      }

      save.addEventListener("click", () => this.saveTypeProjectionRules(type.globalId, element, {
        visible: visible.input.checked,
        color: color.input.value.trim(),
        rank: rank.input.value.trim(),
        infoAttribute: extraControls.info?.input.value.trim() ?? "",
        directed: extraControls.directed?.input.checked ?? false,
        labelVisible: extraControls.labelVisible?.input.checked ?? true
      }));

      row.append(header, rules);
      container.append(row);
    });

  },

  createCheckboxRule(text, checked) {
  const label = this.document.createElement("label");
  label.className = "check-line basis-rule-check";
  const input = this.document.createElement("input");
  input.type = "checkbox";
  input.checked = checked;
  const span = this.document.createElement("span");
  span.textContent = text;
  label.append(input, span);
  return { label, input };

  },

  createTextRule(text, value, placeholder) {
  const label = this.document.createElement("label");
  const span = this.document.createElement("span");
  span.textContent = text;
  const input = this.document.createElement("input");
  input.type = "text";
  input.value = value;
  input.placeholder = placeholder;
  label.append(span, input);
  return { label, input };

  },

  renderRelationList() {
  this.typedEdgeList.replaceChildren();
  const graph = this.buildPhysicalGraph();
  const relations = this.discoverRelationInstances(graph);
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `Инстансы связей в загруженном графе: ${relations.length}`;
  this.typedEdgeList.append(summary);
  relations
    .sort((a, b) => a.relationGlobalId.localeCompare(b.relationGlobalId, "ru"))
    .forEach(relation => {
      const row = this.document.createElement("div");
      row.className = "basis-relation-row";
      const open = this.document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = `${relation.type?.label ?? "связь"}: ${this.displayName(relation.sourceGlobalId)} -> ${this.displayName(relation.targetGlobalId)}`;
      open.title = relation.relationGlobalId;
      open.addEventListener("click", () => {
        this.state.selectedName = relation.relationGlobalId;
        this.render();
        this.setActiveTab("node");
      });
      row.append(open);
      this.typedEdgeList.append(row);
    });

  },

  renderProjectionSummary() {
  const physical = this.buildPhysicalGraph();
  const relations = this.discoverRelationInstances(physical);
  const graph = this.buildGraph();
  const rankedNodes = graph.nodes.filter(node => Number.isFinite(node.viewRank));
  const topRank = rankedNodes.length === 0
    ? ""
    : ` Топ rank: ${this.formatRank(Math.max(...rankedNodes.map(node => node.viewRank)))}.`;
  const basisLabel = this.state.schema.projectionBasis === "empty" ? "пустой базис" : "типовой базис";
  this.projectionSummary.textContent = `Проекция: ${basisLabel}. Загружено: ${physical.nodes.length} узлов, ${physical.edges.length} исходных связей, ${relations.length} типизированных связей.${topRank}`;

  },

  setActiveTab(name) {
  this.document.querySelectorAll(".tab-button").forEach(button => {
    button.classList.toggle("active", button.dataset.tab === name);
  });
  this.document.querySelectorAll(".tab-panel").forEach(panel => {
    panel.classList.toggle("active", panel.id === `tab-${name}`);
  });

  },

  createSvg(name, attrs) {
  const element = this.document.createElementNS(svgNs, name);
  Object.entries(attrs).forEach(([key, value]) => element.setAttribute(key, value));
  return element;

  },

  setStatus(message) {
  this.statusOutput.value = message;
  this.statusOutput.textContent = message;

  },

  setBusy(value) {
  this.state.busy = value;
  this.document.querySelectorAll("button").forEach(button => {
    if (!button.classList.contains("tab-button")) {
      button.disabled = value;
    }
  });
  if (!value) {
    this.updateEditorState();
  }

  },

  updateEditorState() {
  const hasSelection = Boolean(this.state.selectedName && this.state.loaded.has(this.state.selectedName));
  this.saveNodeButton.disabled = this.state.busy || !hasSelection;
  this.deleteNodeButton.disabled = this.state.busy || !hasSelection;
  this.connectForm.querySelector("button").disabled = this.state.busy || !hasSelection;
  if (!this.state.searchAbort) {
    this.setSearchStreaming(false);
  }

  }
};
