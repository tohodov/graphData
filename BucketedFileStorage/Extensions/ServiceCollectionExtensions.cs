using GraphData.BucketedFileStorage.Options;
using GraphData.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.BucketedFileStorage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBucketedFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BucketedFileGraphStorageOptions>()
            .Bind(configuration)
            .PostConfigure(static options =>
            {
                if (string.IsNullOrWhiteSpace(options.RootPath))
                {
                    options.RootPath = Path.Combine(AppContext.BaseDirectory, "bucketed-file-storage");
                }
            });

        services.AddSingleton<IGraphStorage, BucketedFileGraphStorage>();
        return services;
    }

    public static IServiceCollection AddBucketedFileStorage(this IServiceCollection services, Action<BucketedFileGraphStorageOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGraphStorage, BucketedFileGraphStorage>();
        return services;
    }
}
