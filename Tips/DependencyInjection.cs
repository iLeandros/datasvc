// Tips/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataSvc.Tips;

public static class DependencyInjection
{
    public static IServiceCollection AddTipsServices(this IServiceCollection services)
    {
        services.AddSingleton<TipsStore>();
        services.AddSingleton<TipsScraperService>();
        services.AddHostedService<TipsRefreshJob>();
        return services;
    }
}
