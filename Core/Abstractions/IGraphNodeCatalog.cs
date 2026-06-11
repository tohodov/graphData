using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphNodeCatalog
{
    async Task<IReadOnlyCollection<Node>> GetRootNodesAsync() {
#pragma warning disable CS0618
        var nodes = await GetAllNodesAsync();
#pragma warning restore CS0618
        return nodes
            .Where(static node => node.GlobalId.Count() == 1)
            .ToArray();
    }

    [Obsolete("нельзя читать весь граф")]
    Task<IReadOnlyCollection<Node>> GetAllNodesAsync();//TODO удалить, нельзя читать весь граф
}
