import type { components } from "./openapi-types.js";

type Schemas = components["schemas"];

export type AssignNodeTypeRequest = Schemas["AssignNodeTypeRequest"];
export type ChangeEdgeTypeRequest = Schemas["ChangeEdgeTypeRequest"];
export type ConnectNodesRequest = Schemas["ConnectNodesRequest"];
export type CreateNodeRequest = Schemas["CreateNodeRequest"];
export type EdgeResponse = Schemas["EdgeResponse"];
export type GraphObservationBoundaryResponse = Schemas["GraphObservationBoundaryResponse"];
export type GraphObservationResponse = Schemas["GraphObservationResponse"];
export type GraphTraversalRequest = Schemas["GraphTraversalRequest"];
export type NodeResponse = Schemas["NodeResponse"];
export type OperationResponse = Schemas["OperationResponse"];
export type UiBasisResponse = Schemas["UiBasisResponse"];
export type UiSettingsResponse = Schemas["UiSettingsResponse"];
export type UiSystemNodeIdsResponse = Schemas["UiSystemNodeIdsResponse"];
export type UpdateNodeRequest = Schemas["UpdateNodeRequest"];

export type NodeLiteralSearchSelectorRequest =
  Schemas["NodeSearchNodeSelectorRequestNodeLiteralSearchSelectorRequest"];
export type NodeVariableSearchSelectorRequest =
  Schemas["NodeSearchNodeSelectorRequestNodeVariableSearchSelectorRequest"];
export type NodeSearchNodeSelectorRequest =
  | NodeLiteralSearchSelectorRequest
  | NodeVariableSearchSelectorRequest;

export interface AllNodeSearchExpressionRequest {
  kind: "all";
  expressions?: NodeSearchExpressionRequest[];
}

export interface AnyNodeSearchExpressionRequest {
  kind: "any";
  expressions?: NodeSearchExpressionRequest[];
}

export interface ExistsNodeSearchExpressionRequest {
  kind: "exists";
  variables?: string[];
  expression: NodeSearchExpressionRequest;
}

export interface NodeAttributeSearchExpressionRequest {
  kind: "attribute";
  node: NodeSearchNodeSelectorRequest;
  key: string;
  operator?: string;
  value?: string | null;
}

export interface NodeConnectedSearchExpressionRequest {
  kind: "connected";
  left: NodeSearchNodeSelectorRequest;
  right: NodeSearchNodeSelectorRequest;
}

export interface NodeDegreeSearchExpressionRequest {
  kind: "degree";
  node: NodeSearchNodeSelectorRequest;
  operator?: string;
  value?: number;
}

export interface NodeDescendantSearchExpressionRequest {
  kind: "descendant";
  ancestor: NodeSearchNodeSelectorRequest;
  descendant: NodeSearchNodeSelectorRequest;
  minDepth?: number;
  maxDepth?: number;
}

export interface NodeExistsSearchExpressionRequest {
  kind: "node";
  node: NodeSearchNodeSelectorRequest;
}

export interface NodeNameSearchExpressionRequest {
  kind: "name";
  node: NodeSearchNodeSelectorRequest;
  operator?: string;
  value: string;
}

export interface NodeNotSameSearchExpressionRequest {
  kind: "notSame";
  left: NodeSearchNodeSelectorRequest;
  right: NodeSearchNodeSelectorRequest;
}

export interface NodePathSearchExpressionRequest {
  kind: "path";
  left: NodeSearchNodeSelectorRequest;
  right: NodeSearchNodeSelectorRequest;
  minDepth?: number;
  maxDepth?: number;
  includeSelf?: boolean;
}

export interface NodeSameSearchExpressionRequest {
  kind: "same";
  left: NodeSearchNodeSelectorRequest;
  right: NodeSearchNodeSelectorRequest;
}

export interface NodeTextSearchExpressionRequest {
  kind: "text";
  node: NodeSearchNodeSelectorRequest;
  value: string;
}

export interface NotNodeSearchExpressionRequest {
  kind: "not";
  expression: NodeSearchExpressionRequest;
}

export type NodeSearchExpressionRequest =
  | AllNodeSearchExpressionRequest
  | AnyNodeSearchExpressionRequest
  | ExistsNodeSearchExpressionRequest
  | NodeAttributeSearchExpressionRequest
  | NodeConnectedSearchExpressionRequest
  | NodeDegreeSearchExpressionRequest
  | NodeDescendantSearchExpressionRequest
  | NodeExistsSearchExpressionRequest
  | NodeNameSearchExpressionRequest
  | NodeNotSameSearchExpressionRequest
  | NodePathSearchExpressionRequest
  | NodeSameSearchExpressionRequest
  | NodeTextSearchExpressionRequest
  | NotNodeSearchExpressionRequest;

export interface NodeSearchMatchResponse {
  node: NodeResponse;
  bindings?: Record<string, NodeResponse>;
  score?: number;
  matchedBy?: string[];
}

export interface NodeSearchQueryRequest {
  return?: string[];
  where?: NodeSearchExpressionRequest | null;
  limit?: number;
}

export interface SubgraphRequest {
  paths: string[][];
  maxDepth: number;
}

export interface SubgraphResponse {
  nodes?: NodeResponse[];
  edges?: EdgeResponse[];
}
