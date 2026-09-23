import type { GraphNode } from "./GraphNode.js";

type Selector = { kind: "var" | "literal"; name: string };
type Expression = {
  kind: string;
  expressions?: Expression[];
  expression?: Expression;
  variables?: string[];
  node?: Selector;
  left?: Selector;
  right?: Selector;
  ancestor?: Selector;
  descendant?: Selector;
  operator?: string;
  value?: string | number;
  key?: string;
  minDepth?: number;
  maxDepth?: number;
  includeSelf?: boolean;
};
type Query = { return?: string[]; where?: Expression; limit?: number };
type Bindings = Record<string, GraphNode>;

/** Evaluates the editor's query language over semantic snapshots and their incidence edges. */
export function searchTypedGraph(nodes: GraphNode[], query: Query) {
  if (!query || typeof query !== "object" || Array.isArray(query)) throw new Error("Ожидается объект запроса.");
  const returns = query.return ?? ["n"];
  if (!Array.isArray(returns) || !returns.length || returns.some(value => typeof value !== "string" || !value.trim()))
    throw new Error("return должен содержать имена переменных.");
  const limit = query.limit ?? 50;
  if (!Number.isInteger(limit) || limit < 1 || limit > 500) throw new Error("limit должен быть от 1 до 500.");
  const selectorValid = (selector: Selector | undefined) => {
    if (!selector || !["var", "literal"].includes(selector.kind) || typeof selector.name !== "string"
      || selector.kind === "var" && !selector.name.trim()) throw new Error("Некорректный селектор узла.");
  };
  const nonemptyText = (value: unknown, field: string) => {
    if (typeof value !== "string" || !value.trim()) throw new Error(`${field} должен содержать текст.`);
  };
  const validate = (expression: Expression) => {
    if (!expression || typeof expression !== "object" || typeof expression.kind !== "string")
      throw new Error("У выражения отсутствует kind.");
    const operator = expression.operator === undefined ? "equals" : expression.operator;
    if (typeof operator !== "string") throw new Error("Некорректный оператор сравнения.");
    switch (expression.kind) {
      case "all": case "any": {
        const children = expression.expressions ?? [];
        if (!Array.isArray(children) || expression.kind === "any" && !children.length)
          throw new Error("expressions должен содержать выражения.");
        children.forEach(validate);
        return;
      }
      case "exists":
        if (expression.variables !== undefined && (!Array.isArray(expression.variables)
          || expression.variables.some(name => typeof name !== "string" || !name.trim())))
          throw new Error("variables должен содержать имена переменных.");
        // Both scoped and negated expressions must be checked even on an empty graph.
        validate(expression.expression!);
        return;
      case "not": validate(expression.expression!); return;
      case "node": case "name": case "attribute": case "text": case "degree":
        selectorValid(expression.node);
        break;
      case "same": case "notSame": case "connected": case "path":
        selectorValid(expression.left); selectorValid(expression.right);
        break;
      case "descendant":
        selectorValid(expression.ancestor); selectorValid(expression.descendant);
        break;
      default: throw new Error(`Неизвестное выражение '${expression.kind}'.`);
    }
    if (expression.kind === "name" || expression.kind === "attribute") {
      const operators = ["equals", "notequals", "contains", "startswith", "endswith"];
      if (expression.kind === "attribute") { operators.push("exists"); nonemptyText(expression.key, "key"); }
      if (!operators.includes(operator.toLowerCase())) throw new Error(`Неизвестный текстовый оператор '${operator}'.`);
      if (operator.toLowerCase() !== "exists") nonemptyText(expression.value, "value");
    }
    if (expression.kind === "text") nonemptyText(expression.value, "value");
    if (expression.kind === "degree") {
      if (!["equals", "notequals", "greaterthan", "greaterthanorequal", "lessthan", "lessthanorequal"].includes(operator.toLowerCase()))
        throw new Error(`Неизвестный числовой оператор '${operator}'.`);
      if (!Number.isInteger(expression.value) || Number(expression.value) < 0) throw new Error("value должен быть неотрицательным целым числом.");
    }
    if (expression.kind === "path" || expression.kind === "descendant") {
      const min = expression.minDepth ?? 1;
      const max = expression.maxDepth ?? (expression.kind === "path" ? 1 : 8);
      if (!Number.isInteger(min) || !Number.isInteger(max) || min < 0 || max < min)
        throw new Error("Некорректная глубина пути.");
      if (expression.includeSelf !== undefined && typeof expression.includeSelf !== "boolean") throw new Error("includeSelf должен быть логическим значением.");
    }
  };
  if (query.where !== undefined && query.where !== null) validate(query.where);
  const byId = new Map(nodes.map(node => [node.name, node]));
  const free = new Set(returns);
  const collect = (expression: Expression | undefined, result: Set<string>, bound = new Set<string>()) => {
    if (!expression) return;
    // An existential's new bindings stay local, including names omitted from variables.
    if (expression.kind === "exists") return;
    for (const selector of [expression.node, expression.left, expression.right, expression.ancestor, expression.descendant]) {
      if (selector?.kind === "var" && !bound.has(selector.name)) result.add(selector.name);
    }
    for (const child of expression.expressions ?? []) collect(child, result, bound);
    collect(expression.expression, result, bound);
  };
  collect(query.where, free);
  const combinations = (names: string[], bindings: Bindings, visit: (current: Bindings) => boolean): boolean => {
    if (!names.length) return visit(bindings);
    if (!nodes.length) return false;
    const offsets = names.map(() => 0);
    while (true) {
      if (visit({ ...bindings, ...Object.fromEntries(names.map((name, index) => [name, nodes[offsets[index]!]!])) })) return true;
      let position = names.length - 1;
      while (position >= 0 && ++offsets[position]! === nodes.length) {
        offsets[position] = 0;
        position--;
      }
      if (position < 0) return false;
    }
  };
  const resolve = (selector: Selector | undefined, bindings: Bindings) => {
    if (!selector || !["var", "literal"].includes(selector.kind)) throw new Error("Некорректный селектор узла.");
    return selector.kind === "var" ? bindings[selector.name] : byId.get(selector.name);
  };
  const compare = (actual: string | number | undefined, expected: string | number | undefined, operator = "equals") => {
    const a = String(actual ?? "").toLocaleLowerCase();
    const b = String(expected ?? "").toLocaleLowerCase();
    switch (operator.toLowerCase()) {
      case "exists": return actual !== undefined;
      case "equals": return actual !== undefined && a === b;
      case "notequals": return actual !== undefined && a !== b;
      case "contains": return actual !== undefined && a.includes(b);
      case "startswith": return actual !== undefined && a.startsWith(b);
      case "endswith": return actual !== undefined && a.endsWith(b);
      case "greaterthan": return actual !== undefined && Number(actual) > Number(expected);
      case "greaterthanorequal": return actual !== undefined && Number(actual) >= Number(expected);
      case "lessthan": return actual !== undefined && Number(actual) < Number(expected);
      case "lessthanorequal": return actual !== undefined && Number(actual) <= Number(expected);
      default: throw new Error(`Неизвестный оператор '${operator}'.`);
    }
  };
  const path = (left: GraphNode, right: GraphNode, expression: Expression) => {
    const min = expression.minDepth ?? 1;
    const max = expression.maxDepth ?? 1;
    if (left.name === right.name) return min === 0 && expression.includeSelf === true;
    const visited = new Set([left.name]);
    let frontier = [left.name];
    for (let depth = 1; depth <= max && frontier.length; depth++) {
      const next: string[] = [];
      for (const id of frontier) for (const neighbor of byId.get(id)?.neighborIds() ?? []) {
        if (visited.has(neighbor)) continue;
        if (neighbor === right.name) return depth >= min;
        visited.add(neighbor);
        next.push(neighbor);
      }
      frontier = next;
    }
    return false;
  };
  const evaluate = (expression: Expression | undefined, bindings: Bindings): boolean => {
    if (!expression) return true;
    switch (expression.kind) {
      case "all": return (expression.expressions ?? []).every(child => evaluate(child, bindings));
      case "any": return (expression.expressions ?? []).some(child => evaluate(child, bindings));
      case "not": return !evaluate(expression.expression, bindings);
      case "exists": {
        const variables = new Set(expression.variables ?? []);
        collect(expression.expression, variables, new Set(Object.keys(bindings)));
        return combinations([...variables], bindings, nested => evaluate(expression.expression, nested));
      }
      case "node": return Boolean(resolve(expression.node, bindings));
      case "name": {
        const node = resolve(expression.node, bindings);
        return !!node && compare(node.localId, expression.value, expression.operator);
      }
      case "attribute": {
        const attributes = resolve(expression.node, bindings)?.attributes;
        const value = attributes && Object.hasOwn(attributes, expression.key!) ? attributes[expression.key!] : undefined;
        return compare(value, expression.value, expression.operator);
      }
      case "text": {
        const node = resolve(expression.node, bindings);
        if (!node) return false;
        const text = [node.name, ...Object.entries(node.attributes).flat()].join(" ").toLocaleLowerCase();
        return String(expression.value ?? "").toLocaleLowerCase().split(/\s+/).filter(Boolean).every(word => text.includes(word));
      }
      case "degree": {
        const node = resolve(expression.node, bindings);
        return !!node && compare(node.edges.length, expression.value, expression.operator);
      }
      case "same": case "notSame": case "connected": case "path": {
        const left = resolve(expression.left, bindings);
        const right = resolve(expression.right, bindings);
        if (!left || !right) return false;
        if (expression.kind === "same") return left.name === right.name;
        if (expression.kind === "notSame") return left.name !== right.name;
        return expression.kind === "connected" ? left.neighborIds().includes(right.name) : path(left, right, expression);
      }
      case "descendant": {
        const ancestor = resolve(expression.ancestor, bindings);
        const descendant = resolve(expression.descendant, bindings);
        if (!ancestor || !descendant || !descendant.name.startsWith(ancestor.name + "/")) return false;
        const depth = descendant.name.slice(ancestor.name.length + 1).split("/").length;
        return depth >= (expression.minDepth ?? 1) && depth <= (expression.maxDepth ?? 8);
      }
      default: throw new Error(`Неизвестное выражение '${expression.kind}'.`);
    }
  };
  const results: Array<{ node: GraphNode; bindings: Bindings; score: number; matchedBy: string[] }> = [];
  const seen = new Set<string>();
  combinations([...free], {}, bindings => {
    if (!evaluate(query.where, bindings)) return false;
    const key = JSON.stringify(returns.map(name => bindings[name]!.name));
    if (seen.has(key)) return false;
    seen.add(key);
    results.push({ node: bindings[returns[0]!]!, bindings: Object.fromEntries(returns.map(name => [name, bindings[name]!])), score: 1, matchedBy: [] });
    return results.length === limit;
  });
  return results;
}
