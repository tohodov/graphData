namespace SymLinkStorage;

internal class SymLink {
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public required string TargetPath { get; init; }
}
