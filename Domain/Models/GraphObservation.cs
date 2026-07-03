namespace GraphData.Core.Models;

public sealed record GraphObservation(
    string Traversal,
    IReadOnlyCollection<GraphObservationNode> Nodes,
    IReadOnlyCollection<GraphObservationEdge> Edges,
    IReadOnlyCollection<GraphObservationBoundary> Boundary,
    bool Exhaustive);

public sealed record GraphObservationNode(
    string LocalId,
    string InternalId,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record GraphObservationEdge(
    string Node1LocalId,
    string Node1InternalId,
    string Node2LocalId,
    string Node2InternalId,
    string Kind,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record GraphObservationBoundary(
    string InternalId,
    string Reason);
