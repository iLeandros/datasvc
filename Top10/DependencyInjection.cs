// Top10/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataSvc.Top10;

public static class DependencyInjection
{
    public static IServiceCollection AddTop10Services(this IServiceCollection services)
    {
        services.AddSingleton<Top10Store>();
        services.AddSingleton<Top10ScraperService>();
        services.AddHostedService<Top10RefreshJob>();
        return services;
    }
}
