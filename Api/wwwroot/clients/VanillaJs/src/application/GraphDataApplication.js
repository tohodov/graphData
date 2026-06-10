import { GraphWorkspaceState } from "../domain/GraphWorkspaceState.js";
import { GraphDataHttpClient } from "../infrastructure/GraphDataHttpClient.js";
import { graphNodeUseCases } from "./graphNodeUseCases.js";
import { graphSchemaUseCases } from "./graphSchemaUseCases.js";
import { graphQueryUseCases } from "./graphQueryUseCases.js";
import { graphDomainModel } from "../domain/graphDomainModel.js";
import { graphLayoutSimulation } from "../presentation/graphLayoutSimulation.js";
import { graphPresentation } from "../presentation/graphPresentation.js";

export class GraphDataApplication {
  constructor({ document, window, apiClient = new GraphDataHttpClient(window.fetch.bind(window)) } = {}) {
    this.document = document;
    this.window = window;
    this.apiClient = apiClient;
    this.state = new GraphWorkspaceState();

    this.svg = this.requireElement("#graph");
    this.viewport = this.requireElement("#viewport");
    this.rootForm = this.requireElement("#root-form");
    this.rootInput = this.requireElement("#root-input");
    this.fitButton = this.requireElement("#fit-button");
    this.resetButton = this.requireElement("#reset-button");
    this.statusOutput = this.requireElement("#status");
    this.emptyState = this.requireElement("#empty-state");
    this.selectedName = this.requireElement("#selected-name");
    this.selectedRank = this.requireElement("#selected-rank");
    this.attributeEditor = this.requireElement("#attribute-editor");
    this.addAttributeButton = this.requireElement("#add-attribute-button");
    this.saveNodeButton = this.requireElement("#save-node-button");
    this.deleteNodeButton = this.requireElement("#delete-node-button");
    this.createNodeForm = this.requireElement("#create-node-form");
    this.createNodeName = this.requireElement("#create-node-name");
    this.createNodeParent = this.requireElement("#create-node-parent");
    this.createNodeType = this.requireElement("#create-node-type");
    this.connectForm = this.requireElement("#connect-form");
    this.connectTargetName = this.requireElement("#connect-target-name");
    this.connectEdgeType = this.requireElement("#connect-edge-type");
    this.connectEdgeName = this.requireElement("#connect-edge-name");
    this.neighborList = this.requireElement("#neighbor-list");
    this.searchForm = this.requireElement("#search-form");
    this.searchQueryJson = this.requireElement("#search-query-json");
    this.searchSubmitButton = this.requireElement("#search-submit-button");
    this.searchStopButton = this.requireElement("#search-stop-button");
    this.searchResults = this.requireElement("#search-results");
    this.subgraphForm = this.requireElement("#subgraph-form");
    this.subgraphResults = this.requireElement("#subgraph-results");
    this.projectionBasis = this.requireElement("#projection-basis");
    this.basisNodeInput = this.requireElement("#basis-node");
    this.nodeTypeRootInput = this.requireElement("#node-type-root");
    this.edgeTypeRootInput = this.requireElement("#edge-type-root");
    this.relationRootInput = this.requireElement("#relation-root");
    this.loadBasisButton = this.requireElement("#load-basis-button");
    this.ensureBasisButton = this.requireElement("#ensure-basis-button");
    this.refreshTypesButton = this.requireElement("#refresh-types-button");
    this.loadRelationsButton = this.requireElement("#load-relations-button");
    this.projectionSummary = this.requireElement("#projection-summary");
    this.nodeTypeList = this.requireElement("#node-type-list");
    this.assignNodeType = this.requireElement("#assign-node-type");
    this.assignNodeTypeButton = this.requireElement("#assign-node-type-button");
    this.edgeTypeList = this.requireElement("#edge-type-list");
    this.typedEdgeList = this.requireElement("#typed-edge-list");
  }

  start() {
    this.bindTabs();
    this.bindToolbar();
    this.bindNodeForms();
    this.bindSearch();
    this.bindSubgraph();
    this.bindProjection();
    this.bindGraphSurface();

    this.setSearchQueryTemplate("all");
    this.syncBasisInputs();
    this.renderTypeControls();
    this.render();

    const params = new URLSearchParams(this.window.location.search);
    const initialGlobalId = params.get("globalId");
    if (initialGlobalId) {
      this.rootInput.value = initialGlobalId;
      void this.loadRoot(initialGlobalId);
    }
  }

  requireElement(selector) {
    const element = this.document.querySelector(selector);
    if (!element) {
      throw new Error(`GraphData UI element not found: ${selector}`);
    }

    return element;
  }

  bindTabs() {
    this.document.querySelectorAll(".tab-button").forEach(button => {
      button.addEventListener("click", () => this.setActiveTab(button.dataset.tab));
    });
  }

  bindToolbar() {
    this.rootForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.loadRoot(this.rootInput.value.trim());
    });

    this.fitButton.addEventListener("click", () => this.fitView());
    this.resetButton.addEventListener("click", () => {
      this.state.resetGraph();
      this.render();
      this.setStatus("");
    });
  }

  bindNodeForms() {
    this.addAttributeButton.addEventListener("click", () => this.addAttributeRow("", ""));
    this.saveNodeButton.addEventListener("click", () => void this.saveSelectedNode());
    this.deleteNodeButton.addEventListener("click", () => void this.deleteSelectedNode());

    this.createNodeForm.addEventListener("submit", event => {
      event.preventDefault();
      const name = this.createNodeName.value.trim();
      if (!name) {
        this.setStatus("Введите LocalId нового узла");
        return;
      }

      void this.createNode(name, {
        parentGlobalId: this.createNodeParent.value.trim(),
        typeGlobalId: this.createNodeType.value
      });
    });

    this.connectForm.addEventListener("submit", event => {
      event.preventDefault();
      const sourceGlobalId = this.state.selectedName;
      const targetGlobalId = this.connectTargetName.value.trim();
      if (!sourceGlobalId || !targetGlobalId) {
        this.setStatus("Выберите узел и укажите цель связи");
        return;
      }

      void this.connectNodes(sourceGlobalId, targetGlobalId, {
        typeGlobalId: this.connectEdgeType.value,
        relationLocalId: this.connectEdgeName.value.trim()
      });
    });
  }

  bindSearch() {
    this.document.querySelectorAll("[data-query-template]").forEach(button => {
      button.addEventListener("click", () => this.setSearchQueryTemplate(button.dataset.queryTemplate));
    });

    this.searchForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.searchNodes();
    });

    this.searchStopButton.addEventListener("click", () => {
      this.state.searchAbort?.abort();
    });
  }

  bindSubgraph() {
    this.subgraphForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.loadSubgraph();
    });
  }

  bindProjection() {
    this.projectionBasis.addEventListener("change", async () => {
      this.state.schema.projectionBasis = this.projectionBasis.value;
      if (this.state.schema.projectionBasis === "typed") {
        await this.refreshTypes();
      }
      this.render();
      this.runSimulation(18);
    });

    this.loadBasisButton.addEventListener("click", () => void this.loadBasis());
    this.ensureBasisButton.addEventListener("click", () => void this.ensureDefaultBasis());
    this.refreshTypesButton.addEventListener("click", () => void this.refreshTypes());
    this.loadRelationsButton.addEventListener("click", () => void this.loadRelationInstances());
    this.assignNodeTypeButton.addEventListener("click", () => void this.assignSelectedNodeType());
  }

  bindGraphSurface() {
    this.svg.addEventListener("pointerdown", event => {
      if (event.button !== 0 || event.target.closest(".node") || event.target.closest(".edge-button")) {
        return;
      }

      this.svg.setPointerCapture(event.pointerId);
      this.svg.classList.add("dragging");
      this.state.pointer = { x: event.clientX, y: event.clientY };
    });

    this.svg.addEventListener("pointermove", event => {
      if (this.state.dragging) {
        const position = this.state.positions.get(this.state.dragging.name);
        if (!position) {
          return;
        }

        const dx = (event.clientX - this.state.dragging.x) / this.state.view.scale;
        const dy = (event.clientY - this.state.dragging.y) / this.state.view.scale;
        position.x += dx;
        position.y += dy;
        this.state.dragging.x = event.clientX;
        this.state.dragging.y = event.clientY;
        this.render();
        return;
      }

      if (!this.state.pointer) {
        return;
      }

      const dx = event.clientX - this.state.pointer.x;
      const dy = event.clientY - this.state.pointer.y;
      this.state.view.x += dx;
      this.state.view.y += dy;
      this.state.pointer = { x: event.clientX, y: event.clientY };
      this.applyView();
    });

    this.svg.addEventListener("pointerup", event => {
      if (this.state.dragging) {
        this.svg.releasePointerCapture(event.pointerId);
        this.state.dragging = null;
        return;
      }

      if (this.state.pointer) {
        this.svg.releasePointerCapture(event.pointerId);
      }

      this.state.pointer = null;
      this.svg.classList.remove("dragging");
    });

    this.svg.addEventListener("wheel", event => {
      event.preventDefault();
      const rect = this.svg.getBoundingClientRect();
      const mouseX = event.clientX - rect.left;
      const mouseY = event.clientY - rect.top;
      const before = this.screenToGraph(mouseX, mouseY);
      const scale = Math.min(2.8, Math.max(0.25, this.state.view.scale * Math.exp(-event.deltaY * 0.0012)));
      this.state.view.scale = scale;
      this.state.view.x = mouseX - before.x * scale;
      this.state.view.y = mouseY - before.y * scale;
      this.applyView();
    }, { passive: false });
  }

  async apiJson(url, options = {}) {
    return this.apiClient.json(url, options);
  }
}

Object.assign(
  GraphDataApplication.prototype,
  graphDomainModel,
  graphNodeUseCases,
  graphSchemaUseCases,
  graphQueryUseCases,
  graphLayoutSimulation,
  graphPresentation
);
