import type { GraphViewer, GraphViewerOptions } from "../GraphViewer.js";
import { GraphType } from "../domain/GraphType.js";
import { TypedGraphApi, type TypedElement, type TypedType } from "../infrastructure/TypedGraphApi.js";
import { searchTypedGraph } from "../domain/TypedGraphQuery.js";

/** Connects the existing canvas, inspector and operations to explicit graph records. */
export class TypedGraphEditor {
  readonly api: TypedGraphApi;
  elements = new Map<string, TypedElement>();
  types = new Map<string, TypedType>();
  readonly memberFields: HTMLDivElement;
  connectSignature = "";
  readConnectMembers: (() => Record<string, string[]>) | null = null;
  readConnectAttributes: () => Record<string, string> = () => ({});
  readInstanceAttributes: () => Record<string, string> = () => ({});
  readonly instanceFields: HTMLDivElement;
  instanceSignature = "";

  constructor(readonly viewer: GraphViewer) {
    this.api = new TypedGraphApi(viewer.api);
    this.memberFields = viewer.document.createElement("div");
    this.memberFields.id = "connect-members";
    this.memberFields.className = "stack-form";
    this.instanceFields = viewer.document.createElement("div");
    this.instanceFields.id = "create-node-fields";
    this.instanceFields.className = "stack-form";
  }

  async initialize() {
    const v = this.viewer;
    v.graph.semanticMode = true;
    // Filesystem roots and carrier decoding are irrelevant to this graph representation.
    const basisControls = v.basisNodeInput.closest(".panel-block");
    if (basisControls) basisControls.setAttribute("hidden", "");
    v.connectTargetName.insertAdjacentElement("afterend", this.memberFields);
    v.connectEdgeType.addEventListener("change", () => this.renderConnectMembers());
    v.createNodeType.insertAdjacentElement("afterend", this.instanceFields);
    v.createNodeType.addEventListener("change", () => this.renderInstanceFields());
    const form = v.document.querySelector<HTMLFormElement>("#connect-form")!;
    form.addEventListener("submit", event => {
      event.preventDefault();
      event.stopImmediatePropagation();
      void this.createRelationFromForm();
    }, true);
    await this.refreshTypes();
  }

  async run(operation: () => Promise<void>, refreshOnError = false) {
    const v = this.viewer;
    v.setBusy(true);
    try { await operation(); }
    catch (error) {
      let message = (error as Error).message;
      if (refreshOnError) {
        try { await this.refresh(); }
        catch { message += " Не удалось перечитать граф после ошибки."; }
      }
      v.showServerError(error, "Редактирование графа");
      v.setStatus(message);
    } finally { v.setBusy(false); }
  }

  async refreshTypes() {
    await this.run(async () => {
      this.types = new Map((await this.api.types()).map(type => [type.id, type]));
      this.viewer.graph.schema.nodeTypes.clear();
      this.viewer.graph.schema.edgeTypes.clear();
      for (const definition of this.types.values()) {
        let rules = {};
        const saved = this.viewer.window.localStorage.getItem(`graph-view:${definition.id}`);
        if (saved) { try { rules = JSON.parse(saved); } catch { /* Ignore an obsolete view preference. */ } }
        const type = new GraphType({ path: definition.id, localId: definition.id.split("/").at(-1),
          element: definition.kind === "relation" ? "edge" : "node", rank: 50, collapsed: true, directed: true, ...rules });
        const map = definition.kind === "relation" ? this.viewer.graph.schema.edgeTypes : this.viewer.graph.schema.nodeTypes;
        map.set(definition.id, type);
      }
      this.viewer.rebuildProjectionFromBasis("types-loaded");
      this.viewer.renderTypeControls();
      this.renderInstanceFields();
    });
  }

  async readElements() {
    this.elements = new Map((await this.api.list()).map(element => [element.id, element]));
    return this.api.snapshots([...this.elements.values()]);
  }

  async refresh(showAll = false, selected?: string) {
    const v = this.viewer;
    const snapshots = await this.readElements();
    const previous = new Map(v.graph.loaded);
    v.graph.batchPrimitiveChanges("elements-refreshed", () => {
      v.graph.loaded.clear();
      for (const [index, node] of snapshots.entries()) {
        const old = previous.get(node.name);
        node.position = old?.position ?? null;
        node.collapsed = old?.collapsed ?? false;
        node.showed = showAll || node.name === selected || old?.showed === true || !old;
        v.graph.loaded.set(node.name, node);
        if (node.showed && !node.position) v.seedSubgraphPosition(node.name, index, snapshots.length);
      }
    });
    if (selected) v.graph.selectedName = selected;
    if (!v.graph.rootName || !this.elements.has(v.graph.rootName)) v.graph.rootName = snapshots[0]?.name ?? null;
    v.refreshEdgeAngles();
    v.graph.resetRootComponentsFromVisibleGraph(v.graph.visibleGraph());
    v.render();
    v.renderTypeControls();
    v.setEmptyState(snapshots.length ? "Узел не выбран" : "Граф пуст. Создайте первый узел.");
  }

  loadGraph() { return this.run(async () => { await this.refresh(true); this.viewer.fitView(); }); }

  loadNode(id: string, from: string | null = null, select = true) {
    return this.run(async () => {
      const snapshots = await this.readElements();
      const node = snapshots.find(item => item.name === id);
      if (!node) throw new Error(`Узел '${id}' не найден.`);
      this.viewer.storeNodeExpansion(node, from, { select, showed: true });
      this.viewer.render();
      this.viewer.renderTypeControls();
    });
  }

  async ensureLoaded(id: string) {
    if (this.viewer.graph.loaded.has(id)) return;
    const element = await this.api.get(id);
    this.elements.set(id, element);
    this.viewer.storeNodeExpansion(this.api.snapshot(element, [...this.elements.values()]), null, { select: false });
  }

  createNode(id: string, options: GraphViewerOptions) {
    return this.run(async () => {
      await this.api.createInstance(id, options.typeGlobalId ? [options.typeGlobalId] : [], this.readInstanceAttributes());
      for (const linked of options.linkedNodeNames ?? []) {
        if (linked !== id) await this.api.createRelation(this.relationId(), null, { Source: [linked], Target: [id] });
      }
      this.viewer.createNodeName.value = "";
      const wasEmpty = this.viewer.graph.loaded.size === 0;
      await this.refresh(false, id);
      if (wasEmpty) this.viewer.fitView();
      this.viewer.setStatus(`Создан узел «${id}»`);
    }, (options.linkedNodeNames?.length ?? 0) > 0);
  }

  saveAttributes(id: string, attributes: Record<string, string>) {
    return this.run(async () => {
      await this.api.replaceAttributes(id, attributes);
      await this.refresh();
      this.viewer.setStatus(`Сохранён узел «${id}»`);
    });
  }

  assignType(ids: string[], typeId: string) {
    return this.run(async () => {
      for (const id of ids) {
        const element = await this.api.get(id);
        // Assigning a node type retains its existing facets, as in the original editor.
        const concrete = element.typeIds.filter(current => !this.types.get(current)?.isAbstract);
        const typeIds = !typeId ? [] : element.kind === "relation" ? [typeId] : [...new Set([...concrete, typeId])];
        await this.api.replaceTypes(id, typeIds);
      }
      await this.refresh();
      this.viewer.setStatus("Типы сохранены");
    }, ids.length > 1);
  }

  connect(source: string, target: string, options: GraphViewerOptions = {}) {
    return this.run(async () => {
      if (options.typeGlobalId) throw new Error("Укажите именованных участников в форме связи.");
      const id = options.relationLocalId || this.relationId();
      await this.api.createRelation(id, null, { Source: [source], Target: [target] });
      await this.refresh(false, id);
    });
  }

  relationId() { return `relation-${this.viewer.window.crypto.randomUUID()}`; }

  renderConnectMembers() {
    const v = this.viewer;
    const typeId = v.connectEdgeType.value;
    const source = v.selectedNodeNames()[0] ?? "";
    const signature = `${typeId}\0${source}`;
    if (signature === this.connectSignature) return;
    this.connectSignature = signature;
    const type = this.types.get(typeId);
    const members = Object.fromEntries((type?.members ?? [{ name: "Source" }, { name: "Target" }])
      .map((member, index) => [member.name, index === 0 && source ? [source] : []]));
    const editor = this.memberEditor(members, type);
    this.memberFields.replaceChildren(editor.element);
    this.readConnectMembers = editor.read;
    const attributes = this.attributeFields(typeId);
    this.memberFields.append(attributes.element);
    this.readConnectAttributes = attributes.read;
    v.connectTargetName.hidden = true;
    v.connectEdgeName.placeholder = "Название связи (необязательно)";
  }

  createRelationFromForm() {
    return this.run(async () => {
      const v = this.viewer;
      const id = v.connectEdgeName.value.trim() || this.relationId();
      const members = this.readConnectMembers?.() ?? {};
      await this.api.createRelation(id, v.connectEdgeType.value || null, members, this.readConnectAttributes());
      v.connectEdgeName.value = "";
      this.connectSignature = "";
      await this.refresh(false, id);
      this.viewer.setStatus(`Создана связь «${id}»`);
    });
  }

  memberEditor(members: Record<string, string[]>, type?: TypedType) {
    const element = this.viewer.document.createElement("div");
    element.className = "stack-form";
    const inputs: Array<{ name: () => string; input: HTMLTextAreaElement }> = [];
    const definitions = type?.members ?? Object.keys(members).map(name => ({ name, typeId: null, min: 0, max: null }));
    const add = (member: { name: string; typeId?: string | null; max?: number | null }) => {
      const label = this.viewer.document.createElement("label");
      const caption = this.viewer.document.createElement("span");
      caption.textContent = `${member.name}${member.typeId ? ` (${member.typeId.split("/").at(-1)})` : ""}`;
      const input = this.viewer.document.createElement("textarea");
      input.rows = member.max === 1 ? 1 : 2;
      input.dataset.member = member.name;
      input.setAttribute("aria-label", `Участники: ${member.name}`);
      input.placeholder = "По одному идентификатору на строке";
      input.value = (members[member.name] ?? []).join("\n");
      const roleName = this.viewer.document.createElement("input");
      roleName.value = member.name;
      roleName.placeholder = "Название роли";
      roleName.setAttribute("aria-label", "Название роли");
      const row = { name: () => type ? member.name : roleName.value.trim(), input };
      inputs.push(row);
      label.append(type ? caption : roleName, input);
      if (!type) label.append(this.viewer.createCompactButton("Убрать роль", () => {
        inputs.splice(inputs.indexOf(row), 1);
        label.remove();
      }));
      element.append(label);
    };
    definitions.forEach(add);
    if (!type) element.append(this.viewer.createCompactButton("Добавить роль", () => add({ name: "" })));
    return { element, read: () => {
      const names = inputs.map(row => row.name());
      if (names.some(name => !name) || new Set(names).size !== names.length) throw new Error("Укажите неповторяющиеся названия ролей.");
      return Object.fromEntries(inputs.map(({ name, input }) =>
        [name(), input.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean)]));
    } };
  }

  attributeFields(typeId: string, values: Record<string, string> = {}) {
    const element = this.viewer.document.createElement("div");
    element.className = "stack-form";
    const fields = new Map<string, { name: string; kind: string; required: boolean }>();
    const visited = new Set<string>();
    const readType = (id: string) => {
      if (visited.has(id)) return;
      visited.add(id);
      const type = this.types.get(id);
      for (const base of type?.requiredTypeIds ?? []) readType(base);
      for (const field of type?.attributes ?? []) fields.set(field.name, { ...field, required: field.required || fields.get(field.name)?.required === true });
    };
    readType(typeId);
    const inputs: Array<{ input: HTMLInputElement; name: string; required: boolean }> = [];
    for (const field of fields.values()) {
      const control = this.viewer.createTextRule(`${field.name}${field.required ? " *" : ""}`, values[field.name] ?? "", field.kind);
      control.input.setAttribute("aria-label", `Атрибут: ${field.name}`);
      element.append(control.label);
      inputs.push({ input: control.input, name: field.name, required: field.required });
    }
    return { element, read: () => Object.fromEntries(inputs.filter(row => row.required || row.input.value !== "").map(row => [row.name, row.input.value])) };
  }

  renderInstanceFields() {
    const typeId = this.viewer.createNodeType.value;
    if (this.instanceSignature === typeId && this.instanceFields.childNodes.length) return;
    this.instanceSignature = typeId;
    const fields = this.attributeFields(typeId);
    this.instanceFields.replaceChildren(fields.element);
    this.readInstanceAttributes = fields.read;
  }

  createDetails(id: string) {
    const v = this.viewer;
    const element = this.elements.get(id);
    const details = v.document.createElement("div");
    details.className = "selection-details";
    if (!element) { details.textContent = "Элемент не загружен"; return details; }
    details.append(v.createDetailsList([["Идентификатор", id], ["Вид", element.kind === "relation" ? "Отношение" : "Узел"],
      ["Типы", element.typeIds.join(", ") || "Без типа"]]));
    const attributes = v.createAttributeEditor(element.attributes);
    details.append(v.createDetailsSection("Атрибуты", attributes.element, [
      v.createCompactButton("Добавить", () => attributes.addRow("", "")),
      v.createCompactButton("Сохранить", () => void this.saveAttributes(id, attributes.read()))
    ]));
    const typeSelect = v.document.createElement("select");
    v.renderTypeSelect(typeSelect, element.kind === "relation" ? v.graph.schema.edgeTypes : v.graph.schema.nodeTypes, "Без типа");
    if (element.kind === "relation") typeSelect.value = element.typeIds[0] ?? "";
    details.append(v.createDetailsSection("Тип", typeSelect, element.kind === "relation" ? [] : [
      v.createCompactButton("Назначить", () => void this.assignType([id], typeSelect.value))
    ]));
    if (element.kind === "relation") {
      let members = this.memberEditor(element.members, this.types.get(element.typeIds[0] ?? ""));
      const container = v.document.createElement("div");
      container.append(members.element);
      typeSelect.addEventListener("change", () => {
        const type = this.types.get(typeSelect.value);
        members = this.memberEditor(members.read(), type);
        container.replaceChildren(members.element);
        const currentAttributes = attributes.read();
        for (const field of type?.attributes ?? []) {
          if (!(field.name in currentAttributes)) attributes.addRow(field.name, "");
        }
      });
      details.append(v.createDetailsSection("Участники", container, [v.createCompactButton("Сохранить отношение", () => {
        void this.run(async () => {
          await this.api.replaceRelation(id, typeSelect.value || null, members.read(), attributes.read());
          await this.refresh();
          v.setStatus("Отношение сохранено");
        });
      })]));
    }
    return details;
  }

  deleteSelection(ids: string[], edges: Array<{ relationGlobalId?: string; memberName?: string; node2InternalId?: string }>) {
    return this.run(async () => {
      await this.readElements();
      const selected = new Set(ids);
      const replacements = new Map<string, Record<string, string[]>>();
      for (const edge of edges) {
        const id = edge.relationGlobalId;
        if (!id || selected.has(id)) continue;
        if (!edge.memberName) { selected.add(id); continue; }
        const element = this.elements.get(id)!;
        const members = replacements.get(id) ?? structuredClone(element.members);
        members[edge.memberName] = (members[edge.memberName] ?? []).filter(participant => participant !== edge.node2InternalId);
        replacements.set(id, members);
      }
      // Check references before deleting anything in a multiple selection.
      for (const element of this.elements.values()) {
        if (selected.has(element.id)) continue;
        const members = replacements.get(element.id) ?? element.members;
        if (Object.values(members).some(values => values.some(id => selected.has(id))))
          throw new Error(`На выбранный узел ссылается «${element.id}». Сначала измените или выберите это отношение для удаления.`);
      }
      // Validate every member removal before the first request can change a relation.
      for (const [id, members] of replacements) {
        if (selected.has(id)) continue;
        const relation = this.elements.get(id)!;
        for (const typeId of relation.typeIds) {
          const type = this.types.get(typeId);
          if (!type) throw new Error(`Обновите типы перед изменением отношения «${id}»: тип «${typeId}» не загружен.`);
          for (const member of type.members) {
            const count = members[member.name]?.length ?? 0;
            if (count < member.min || member.max !== null && count > member.max)
              throw new Error(`Нельзя удалить выбранных участников: роль «${member.name}» отношения «${id}» требует ${member.min}..${member.max ?? "∞"} участников.`);
          }
        }
      }
      for (const [id, members] of replacements) if (!selected.has(id)) await this.api.replaceMembers(id, members);
      while (selected.size) {
        const id = [...selected].find(candidate => ![...selected].some(other => other !== candidate &&
          Object.values(this.elements.get(other)?.members ?? {}).some(values => values.includes(candidate))));
        if (!id) throw new Error("Выбранные отношения образуют цикл.");
        await this.api.delete(id);
        selected.delete(id);
      }
      this.viewer.graph.clearSelection();
      await this.refresh();
      this.viewer.setStatus("Выбранные элементы удалены");
    }, true);
  }

  search() {
    return this.run(async () => {
      const snapshots = await this.readElements();
      const query = JSON.parse(this.viewer.searchQueryJson.value);
      const matches = searchTypedGraph(snapshots, query);
      this.viewer.renderSearchResults(matches);
      this.viewer.setStatus(`Найдено решений: ${matches.length}`);
    });
  }

  loadSubgraph() {
    return this.run(async () => {
      const nodes = await this.readElements();
      const v = this.viewer;
      const roots = v.parseCsv(v.requireElement<HTMLInputElement>("#subgraph-roots").value);
      const depth = v.readNumber("#subgraph-depth", 1);
      const byId = new Map(nodes.map(node => [node.name, node]));
      const selected = new Set<string>(roots.length ? roots : byId.keys());
      for (const id of roots) if (!byId.has(id)) throw new Error(`Узел '${id}' не найден.`);
      let frontier = [...selected];
      for (let step = 0; step < depth && frontier.length; step++) {
        const next: string[] = [];
        for (const id of frontier) for (const neighbor of byId.get(id)?.neighborIds() ?? []) {
          if (!selected.has(neighbor)) { selected.add(neighbor); next.push(neighbor); }
        }
        frontier = next;
      }
      const response = { nodes: nodes.filter(node => selected.has(node.name)), edges: [] };
      v.loadSubgraphIntoViewer(response, roots);
      v.renderSubgraphResults(response);
    });
  }

  loadRelations() {
    return this.run(async () => { await this.refresh(); });
  }

  renderRelationList() {
    const v = this.viewer;
    v.typedEdgeList.replaceChildren();
    for (const relation of this.elements.values()) {
      if (relation.kind !== "relation") continue;
      const open = v.document.createElement("button");
      open.type = "button";
      open.className = "result-row";
      open.textContent = `${relation.id}: ${Object.entries(relation.members).map(([name, ids]) => `${name} = ${ids.join(", ")}`).join("; ")}`;
      open.addEventListener("click", () => void this.loadNode(relation.id));
      v.typedEdgeList.append(open);
    }
  }

  saveViewRules(id: string, element: string, rules: Record<string, unknown>) {
    this.viewer.window.localStorage.setItem(`graph-view:${id}`, JSON.stringify(rules));
    this.viewer.applyTypeProjectionRules(id, element, rules);
    this.viewer.setStatus("Настройки отображения сохранены");
  }

  showType(id: string) {
    const definition = this.types.get(id);
    if (!definition) return;
    this.viewer.expandedBasisRuleKeys.add(this.viewer.basisRuleKey(definition.kind === "relation" ? "edge" : "node", id));
    this.viewer.renderTypeControls();
    const block = this.viewer.document.createElement("pre");
    block.className = "type-definition";
    block.textContent = JSON.stringify(definition, null, 2);
    const list = definition.kind === "relation" ? this.viewer.edgeTypeList : this.viewer.nodeTypeList;
    list.append(block);
  }
}
