namespace GraphData.Compiler.Model;

/// <summary>
/// Finite, explicit input of the compiler. Directions and relation types must
/// already be resolved by the projection that produced the snapshot.
/// </summary>
public sealed record TypedGraphSnapshot(
    IReadOnlyList<TypedNodeSnapshot> Nodes,
    IReadOnlyList<TypedRelationSnapshot> Relations);

public sealed record TypedNodeSnapshot(
    string Key,
    IReadOnlyList<string> TypeKeys,
    IReadOnlyList<float> Features);

public sealed record TypedRelationSnapshot(
    string Key,
    string TypeKey,
    string SourceKey,
    string TargetKey,
    float Weight = 1f);
