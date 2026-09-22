using GraphData.Core.Extensions;
using SemanticStorage;
using Storage;

namespace GraphData.Api.Runtime;

public enum GraphStorageMode { Legacy, Semantic }

public sealed record GraphStorageSelection(GraphStorageMode Mode);

public static class GraphStorageRegistration
{
    public static IServiceCollection AddConfiguredGraphStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var mode = ReadMode(configuration["GraphStorage:Mode"]);
        services.AddSingleton(new GraphStorageSelection(mode));
        if (mode == GraphStorageMode.Legacy)
        {
            services.AddDomain();
            services.AddSymLinkStorage(configuration.GetSection("GraphStorage"));
            services.AddCarrierTypedGraph();
        }
        else
        {
            var root = configuration["SemanticGraphStorage:RootPath"];
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Semantic mode requires a separate SemanticGraphStorage:RootPath.");

            var legacyRoot = configuration["GraphStorage:RootPath"];
            if (!string.IsNullOrWhiteSpace(legacyRoot) && RootsOverlap(root, legacyRoot))
                throw new InvalidOperationException("SemanticGraphStorage:RootPath must not overlap the legacy GraphStorage:RootPath.");

            services.AddSemanticRecordStorage(root);
        }

        return services;
    }

    static GraphStorageMode ReadMode(string? value) => value?.ToLowerInvariant() switch
    {
        null or "legacy" => GraphStorageMode.Legacy,
        "semantic" => GraphStorageMode.Semantic,
        _ => throw new InvalidOperationException($"Unknown GraphStorage:Mode '{value}'. Expected 'legacy' or 'semantic'.")
    };

    static bool RootsOverlap(string first, string second)
    {
        var firstPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var secondPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase)
            || firstPath.StartsWith(WithTrailingSeparator(secondPath), StringComparison.OrdinalIgnoreCase)
            || secondPath.StartsWith(WithTrailingSeparator(firstPath), StringComparison.OrdinalIgnoreCase);
    }

    static string WithTrailingSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
