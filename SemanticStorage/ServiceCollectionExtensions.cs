using Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticStorage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSemanticGraphStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SemanticGraphStorageOptions>()
            .Bind(configuration);
        return services.AddSemanticGraphStorageCore();
    }

    public static IServiceCollection AddSemanticGraphStorage(
        this IServiceCollection services,
        Action<SemanticGraphStorageOptions> configure)
    {
        services.Configure(configure);
        return services.AddSemanticGraphStorageCore();
    }

    private static IServiceCollection AddSemanticGraphStorageCore(this IServiceCollection services)
    {
        services.AddSingleton<SemanticFileGraphStorage>();
        services.AddSingleton<IGraphStorage>(static provider =>
            provider.GetRequiredService<SemanticFileGraphStorage>());
        services.AddSingleton<ISemanticGraphStorage>(static provider =>
            provider.GetRequiredService<SemanticFileGraphStorage>());
        return services;
    }
}
