namespace GraphData.Api.Models;

public sealed class TypeCatalogRequest
{
    public IReadOnlyCollection<IReadOnlyCollection<string>> Roots { get; init; } =
        Array.Empty<IReadOnlyCollection<string>>();
}
