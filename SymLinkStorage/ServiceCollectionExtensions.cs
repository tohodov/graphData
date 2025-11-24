using GraphData.Core.Abstractions;
using GraphData.SymLinkStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SymLinkStorage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymLinkStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NtfsGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
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
