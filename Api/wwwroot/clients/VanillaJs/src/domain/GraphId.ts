export class GraphId {
  static parse(value: string): string[] {
    return String(value ?? "").split("/").filter(Boolean);
  }

  static toQuery(value: string): string {
    return GraphId.parse(value)
      .map(segment => "globalId=" + encodeURIComponent(segment))
      .join("&");
  }

  static localId(globalId: string): string {
    const parts = GraphId.parse(globalId);
    return parts[parts.length - 1] ?? String(globalId ?? "");
  }

  static isChildOf(globalId: string, parentGlobalId: string): boolean {
    return globalId === parentGlobalId || String(globalId).startsWith(parentGlobalId + "/");
  }
}
