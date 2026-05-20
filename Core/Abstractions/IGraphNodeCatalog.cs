using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphNodeCatalog
{
    Task<IReadOnlyCollection<Node>> GetAllNodesAsync();
}
