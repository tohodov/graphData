using GraphData.Core.Abstractions;
using GraphData.NtfsStorage.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.NtfsStorage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNtfsGraphStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NtfsGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootPath))
                {
                    options.RootPath = Path.Combine(AppContext.BaseDirectory, "graph-data");
                }
            });

        services.AddSingleton<IGraphStorage, SymLinkGraphStorage>();
        return services;
    }

    public static IServiceCollection AddNtfsGraphStorage(this IServiceCollection services, Action<NtfsGraphStorageOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGraphStorage, SymLinkGraphStorage>();
        return services;
    }
}
