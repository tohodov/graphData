namespace Abstractions;

internal interface IGraphNodeCatalog
{
    [Obsolete("нельзя читать весь граф")]
    Task<IReadOnlyCollection<NodeState>> GetAllNodesAsync();//TODO удалить, нельзя читать весь граф
}
