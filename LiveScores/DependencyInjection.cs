// LiveScores/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;

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
using DataSvc.Parsed;
using DataSvc.Details;

namespace DataSvc.LiveScores;

public static class DependencyInjection
{
    public static IServiceCollection AddLiveScoresServices(this IServiceCollection services)
    {
        services.AddSingleton<LiveScoresStore>();            // already exists in your project
        services.AddSingleton<LiveScoresScraperService>();   // moved from Program.cs
        services.AddHostedService<LiveScoresRefreshJob>();   // moved from Program.cs
        return services;
    }
}
