using Abstractions;

namespace GraphData.Core.Services;

internal static class FixedGraphTopology {
    internal static readonly NodeLocalId NodeTypesLocalId = new("NodeTypes");

    internal static NodePath NodeTypesPath => new(NodeTypesLocalId);

    internal static InternalId NodeTypesId => new(NodeTypesPath);

    internal static NodePath NodeTypePath(NodeLocalId typeId) => new(NodeTypesLocalId, typeId);

    internal static InternalId NodeTypeId(NodeLocalId typeId) => new(NodeTypesId.Concat([typeId]));
}
