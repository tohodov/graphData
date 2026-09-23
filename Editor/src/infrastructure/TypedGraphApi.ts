import { GraphApi } from "./GraphApi.js";
import { GraphNode } from "../domain/GraphNode.js";
import { GraphEdge } from "../domain/GraphEdge.js";
import { GraphId } from "../domain/GraphId.js";

export type TypedElementKind = "instance" | "relation";
export type TypedMemberDefinition = { name: string; typeId: string | null; min: number; max: number | null };
export type TypedAttributeDefinition = { name: string; kind: string; required: boolean };
export type TypedType = {
  id: string;
  kind: TypedElementKind;
  isAbstract: boolean;
  requiredTypeIds: string[];
  members: TypedMemberDefinition[];
  attributes: TypedAttributeDefinition[];
};
export type TypedElement = {
  id: string;
  kind: TypedElementKind;
  typeIds: string[];
  attributes: Record<string, string>;
  members: Record<string, string[]>;
};

/** Translates semantic records to the existing editor's view model, without storage carrier nodes. */
export class TypedGraphApi {
  constructor(readonly api: GraphApi) {}

  async list(): Promise<TypedElement[]> {
    const elements = await this.read<TypedElement[]>("/elements/all");
    return elements.map(element => this.normalizeElement(element));
  }

  async types(): Promise<TypedType[]> {
    const types = await this.read<TypedType[]>("/types");
    return types.map(type => ({ ...type, kind: this.normalizeKind(type.kind) }));
  }

  async get(id: string): Promise<TypedElement> {
    return this.normalizeElement(await this.read<TypedElement>("/elements?id=" + encodeURIComponent(id)));
  }

  async createInstance(id: string, typeIds: string[] = [], attributes: Record<string, string> = {}): Promise<TypedElement> {
    return this.writeElement("/instances", "POST", { id, typeIds, attributes });
  }

  async createRelation(id: string, typeId: string | null, members: Record<string, string[]>, attributes: Record<string, string> = {}): Promise<TypedElement> {
    return this.writeElement("/relations", "POST", { id, typeId, members, attributes });
  }

  async replaceAttributes(id: string, attributes: Record<string, string>): Promise<TypedElement> {
    return this.writeElement("/elements/attributes?id=" + encodeURIComponent(id), "PUT", { attributes });
  }

  async replaceTypes(id: string, typeIds: string[]): Promise<TypedElement> {
    return this.writeElement("/elements/types?id=" + encodeURIComponent(id), "PUT", { typeIds });
  }

  async replaceMembers(id: string, members: Record<string, string[]>): Promise<TypedElement> {
    return this.writeElement("/relations/members?id=" + encodeURIComponent(id), "PUT", { members });
  }

  async replaceRelation(id: string, typeId: string | null, members: Record<string, string[]>, attributes: Record<string, string>): Promise<TypedElement> {
    return this.writeElement("/relations?id=" + encodeURIComponent(id), "PUT", { typeId, members, attributes });
  }

  async delete(id: string): Promise<void> {
    await this.api.json("/api/typed-graph/elements?id=" + encodeURIComponent(id), { method: "DELETE", expectJson: false });
  }

  /** Include all loaded elements so each node receives its incoming and outgoing incidence edges. */
  snapshots(elements: readonly TypedElement[]): GraphNode[] {
    const nodes = elements.map(element => this.node(element));
    const byId = new Map(nodes.map(node => [node.internalId, node]));
    for (const element of elements) {
      if (element.kind !== "relation") continue;
      for (const [memberName, participants] of Object.entries(element.members)) {
        for (const participantId of participants) {
          const edge = new GraphEdge({
            node1InternalId: element.id,
            node2InternalId: participantId,
            relationGlobalId: element.id,
            memberName,
            typeGlobalId: element.typeIds[0] ?? null,
            node1LocalId: element.id,
            node2LocalId: participantId,
            neighborLocalIdsByAnchor: { [element.id]: participantId, [participantId]: element.id },
            label: memberName,
            directed: true
          });
          byId.get(element.id)?.edges.push(edge);
          if (participantId !== element.id) byId.get(participantId)?.edges.push(edge);
        }
      }
    }
    return nodes;
  }

  snapshot(element: TypedElement, elements: readonly TypedElement[] = [element]): GraphNode {
    const withElement = elements.filter(existing => existing.id !== element.id).concat(element);
    return this.snapshots(withElement).find(node => node.internalId === element.id)!;
  }

  private node(element: TypedElement): GraphNode {
    return new GraphNode({
      internalId: element.id,
      path: element.id,
      localId: GraphId.localId(element.id),
      displayName: element.attributes.label || element.attributes.name || GraphId.localId(element.id),
      attributes: element.attributes,
      semanticKind: element.kind,
      typeIds: element.typeIds,
      members: element.members
    });
  }

  private async read<T>(path: string): Promise<T> {
    const response = await this.api.json<T>("/api/typed-graph" + path);
    if (response === null) throw new Error("Сервер вернул пустой ответ графа.");
    return response;
  }

  private async writeElement(path: string, method: string, body: unknown): Promise<TypedElement> {
    const response = await this.api.json<TypedElement>("/api/typed-graph" + path, { method, body: JSON.stringify(body) });
    if (response === null) throw new Error("Сервер не вернул сохранённый элемент графа.");
    return this.normalizeElement(response);
  }

  private normalizeElement(element: TypedElement): TypedElement {
    return { ...element, kind: this.normalizeKind(element.kind) };
  }

  private normalizeKind(kind: string): TypedElementKind {
    const normalized = kind.toLowerCase();
    if (normalized !== "instance" && normalized !== "relation") throw new Error("Неизвестный вид элемента графа: " + kind);
    return normalized;
  }
}
