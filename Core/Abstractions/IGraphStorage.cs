using GraphData.Core.Models;

namespace GraphData.Core.Abstractions;

public interface IGraphStorage
{
    Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null);
    Task<Node?> Get(string basisNodeName) => Get(null, basisNodeName);
    Task<Node?> Get(Node? parent, string subNodeName);
    Task<Node?> Get(NodeQuery query);
    Task Update(string subNodeName, IDictionary<string, string> attributes, Node? parent = null);
    Task Delete(string subNodeName, Node? parent = null);
    Task Connect(Node sourceNode, Node targetNode);
    Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node);
    Task<Subgraph> GetSubgraphAsync(SubgraphQuery query);
}
