export class GraphId {
  static parse(value) {
    return String(value ?? "").split("/").filter(Boolean);
  }

  static toQuery(value) {
    return GraphId.parse(value)
      .map(segment => "globalId=" + encodeURIComponent(segment))
      .join("&");
  }

  static localId(globalId) {
    const parts = GraphId.parse(globalId);
    return parts[parts.length - 1] ?? String(globalId ?? "");
  }

  static isChildOf(globalId, parentGlobalId) {
    return globalId === parentGlobalId || String(globalId).startsWith(parentGlobalId + "/");
  }
}
