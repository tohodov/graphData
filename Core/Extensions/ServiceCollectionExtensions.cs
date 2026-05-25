using GraphData.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGraphCore(this IServiceCollection services)
    {
        services.AddScoped<GraphSearchService, GraphSearchService>();
        return services;
    }
}
