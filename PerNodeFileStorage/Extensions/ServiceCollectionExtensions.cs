using GraphData.Core.Abstractions;
using GraphData.PerNodeFileStorage.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.PerNodeFileStorage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPerNodeFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PerNodeFileGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootPath))
                {
                    options.RootPath = Path.Combine(AppContext.BaseDirectory, "per-node-file-storage");
                }
            });

        services.AddSingleton<IGraphStorage, PerNodeFileGraphStorage>();
        return services;
    }

    public static IServiceCollection AddPerNodeFileStorage(this IServiceCollection services, Action<PerNodeFileGraphStorageOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGraphStorage, PerNodeFileGraphStorage>();
        return services;
    }
}
