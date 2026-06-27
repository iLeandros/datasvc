// Tips/Endpoints.cs
using System;
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


namespace DataSvc.Tips;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapTipsEndpoints(this IEndpointRouteBuilder app)
    {
        // /data/tips -> current snapshot groups (same behavior as before)
        app.MapGet("/data/tips", ([FromServices] TipsStore store) =>
        {
            var s = store.Current;
            if (s is null || s.Payload is null)
                return Results.NotFound(new { message = "No data yet" });

            return Results.Json(s.Payload.TableDataGroup);
        });

        // /data/tips/dates -> list available per-date files
        app.MapGet("/data/tips/dates", () =>
        {
            var center = ScraperConfig.TodayLocal();
            return Results.Ok(new
            {
                window = new {
                    today = center.ToString("yyyy-MM-dd"),
                    from  = center.AddDays(-3).ToString("yyyy-MM-dd"),
                    to    = center.ToString("yyyy-MM-dd")
                },
                dates = TipsPerDateFiles.ListDates()
            });
        });

        // /data/tips/date/{yyyy-MM-dd}
        app.MapGet("/data/tips/date/{date}", (string date) =>
        {
            if (!DateOnly.TryParse(date, out var d))
                return Results.BadRequest(new { message = "invalid date", expectedFormat = "yyyy-MM-dd" });

            var snap = TipsPerDateFiles.Load(d);
            if (snap?.Payload?.TableDataGroup is null)
                return Results.NotFound(new { message = "No tips for date", date });

            return Results.Json(snap.Payload.TableDataGroup);
        });

        return app;
    }
}
