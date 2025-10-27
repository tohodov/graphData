using System;
using System.IO;
using GraphData.Core.Abstractions;
using GraphData.SubgraphStorage;
using GraphData.SubgraphStorage.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.SubgraphStorage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLargeSubgraphStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LargeSubgraphGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootPath))
                {
                    options.RootPath = Path.Combine(AppContext.BaseDirectory, "subgraph-storage");
                }
            });

        services.AddSingleton<IGraphStorage, LargeSubgraphGraphStorage>();
        return services;
    }

    public static IServiceCollection AddLargeSubgraphStorage(this IServiceCollection services, Action<LargeSubgraphGraphStorageOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGraphStorage, LargeSubgraphGraphStorage>();
        return services;
    }

    public static IServiceCollection AddRandomAccessSubgraphStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RandomAccessGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootPath))
                {
                    options.RootPath = Path.Combine(AppContext.BaseDirectory, "random-access-storage");
                }
            });

        services.AddSingleton<IGraphStorage, RandomAccessGraphStorage>();
        return services;
    }

    public static IServiceCollection AddRandomAccessSubgraphStorage(this IServiceCollection services, Action<RandomAccessGraphStorageOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGraphStorage, RandomAccessGraphStorage>();
        return services;
    }
}
