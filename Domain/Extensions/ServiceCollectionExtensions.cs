using GraphData.Core.Abstractions;
using GraphData.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDomain(this IServiceCollection services) {
        services.AddScoped<GraphSearchService>(static provider =>
            new GraphSearchService(provider.GetRequiredService<IGraphStorage>()));
        services.AddScoped<GraphService>(static provider =>
            new GraphService(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphSearchService>()));
        return services;
    }
}
