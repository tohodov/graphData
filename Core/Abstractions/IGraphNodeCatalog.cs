using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphNodeCatalog
{
    [Obsolete("нельзя читать весь граф")]
    Task<IReadOnlyCollection<Node>> GetAllNodesAsync();//TODO удалить, нельзя читать весь граф
}
