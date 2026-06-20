using Abstractions;

namespace GraphData.Core.Models;

public interface IGraphNodeType
{
    static abstract InternalId StaticTypeId { get; }
}
