// Top10/Endpoints.cs
using System;
using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
using DataSvc.LiveScores;


namespace DataSvc.Top10;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapTop10Endpoints(this IEndpointRouteBuilder app)
    {
        // /data/top10 -> current-day Top10 groups
        app.MapGet("/data/top10", ([FromServices] Top10Store store) =>
        {
            var s = store.Current;
            if (s is null || s.Payload is null)
                return Results.NotFound(new { message = "No data yet" });

            var groups = s.Payload.TableDataGroup ?? new ObservableCollection<TableDataGroup>();
            return Results.Json(groups);
        });

        // list available per-date files
        app.MapGet("/data/top10/dates", () =>
        {
            var center = ScraperConfig.TodayLocal();
            return Results.Ok(new
            {
                window = new
                {
                    today = center.ToString("yyyy-MM-dd"),
                    from = center.AddDays(-3).ToString("yyyy-MM-dd"),
                    to = center.ToString("yyyy-MM-dd")
                },
                dates = Top10PerDateFiles.ListDates()
            });
        });

        // /data/top10/date/{yyyy-MM-dd}
        app.MapGet("/data/top10/date/{date}", (string date) =>
        {
            if (!DateOnly.TryParse(date, out var d))
                return Results.BadRequest(new { message = "invalid date", expectedFormat = "yyyy-MM-dd" });

            var snap = Top10PerDateFiles.Load(d);
            if (snap?.Payload?.TableDataGroup is null)
                return Results.NotFound(new { message = "No top10 for date", date });

            return Results.Json(snap.Payload.TableDataGroup);
        });

        return app;
    }
}
