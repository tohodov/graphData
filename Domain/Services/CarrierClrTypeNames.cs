using GraphData.Core.Models;

namespace GraphData.Core.Services;

/// <summary>Explicit compatibility for CLR names persisted before the carrier terminology change.</summary>
internal static class CarrierClrTypeNames
{
    static readonly IReadOnlyDictionary<string, Type> LegacyNames = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase) {
        ["Node"] = typeof(CarrierNode),
        ["Edge"] = typeof(CarrierEdge),
        ["Graph"] = typeof(CarrierGraph),
        ["GraphData.Core.Models.Subgraph"] = typeof(CarrierSubgraph),
        ["GraphData.Core.Services.GraphService"] = typeof(CarrierGraphService),
        ["GraphData.Core.Services.GraphSearchService"] = typeof(CarrierGraphSearchService),
        ["GraphData.Core.Services.GraphBackupService"] = typeof(CarrierGraphBackupService)
    };

    public static Type? Resolve(string name) => Type.GetType(name, assemblyResolver: null,
        typeResolver: (assembly, typeName, ignoreCase) => {
            if ((assembly is null || assembly == typeof(CarrierNode).Assembly) && LegacyNames.TryGetValue(typeName, out var legacy))
                return legacy;
            return assembly?.GetType(typeName, throwOnError: false, ignoreCase)
                ?? Type.GetType(typeName, throwOnError: false, ignoreCase);
        }, throwOnError: false, ignoreCase: true);

    public static string Serialize(Type type) {
        // Continue writing the established names so a carrier class rename is not a disk-format migration.
        foreach (var (name, renamed) in LegacyNames)
            if (type == renamed)
                return $"{name}, {type.Assembly.FullName}";
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }
}
