// Details/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth; // AuthController + SessionAuthHandler namespace
using DataSvc.MainHelpers; // MainHelpers
using DataSvc.Likes; // MainHelpers
using DataSvc.Services; // Services
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.MainHelpers;

namespace DataSvc.Details;

public static class DependencyInjection
{
    public static IServiceCollection AddDetailsServices(this IServiceCollection services)
    {
        // in-memory cache + scraper + hosted refresh job(s)
        services.AddSingleton<DetailsStore>();
        services.AddSingleton<DetailsScraperService>();
        services.AddHostedService<DetailsRefreshJob>();

        // if you’re already using this service (present in Program.cs), keep it here:
        services.AddSingleton<DetailsRefreshService>();

        return services;
    }
}
