// LiveScores/Endpoints.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace DataSvc.LiveScores;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapLiveScoresEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /data/livescores/status
        app.MapGet("/data/livescores/status", ([FromServices] LiveScoresStore store) =>
        {
            var dates = store.Dates();
            return Results.Json(new { totalDates = dates.Count, dates, lastSavedUtc = store.LastSavedUtc });
        });

        // GET /data/livescores/dates (+stats per date)
        app.MapGet("/data/livescores/dates", ([FromServices] LiveScoresStore store) =>
        {
            var center = ScraperConfig.TodayLocal();
            var tz = ScraperConfig.TimeZone;

            var dates = (store.Dates() ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var stats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var dateStr in dates)
            {
                var day = store.Get(dateStr);
                int total = 0, live = 0, finished = 0, scheduled = 0, other = 0;

                if (day is not null)
                {
                    var items = GetEnumerable(day, "Items", "Matches", "Games");
                    if (items is not null)
                    {
                        foreach (var item in items)
                        {
                            total++;
                            var status = GetString(item, "Status", "State", "Phase");
                            var st = (status ?? "").Trim().ToLowerInvariant();

                            if (st is "live" or "inplay" or "in_play" or "playing") live++;
                            else if (st is "finished" or "ended" or "ft" or "fulltime" or "full_time") finished++;
                            else if (st is "scheduled" or "upcoming" or "not_started" or "ns" or "pre") scheduled++;
                            else other++;
                        }
                    }
                }

                stats[dateStr] = new { total, live, finished, scheduled, other };
            }

            return Results.Ok(new
            {
                tz,
                center = center.ToString("yyyy-MM-dd"),
                dates,
                stats,
                lastSavedUtc = store.LastSavedUtc
            });

            // local helpers
            static System.Collections.IEnumerable? GetEnumerable(object obj, params string[] names)
            {
                var t = obj.GetType();
                foreach (var n in names)
                    if (t.GetProperty(n)?.GetValue(obj) is System.Collections.IEnumerable e) return e;
                return null;
            }
            static string? GetString(object obj, params string[] names)
            {
                var t = obj.GetType();
                foreach (var n in names)
                {
                    var v = t.GetProperty(n)?.GetValue(obj)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                return null;
            }
        });

        // GET /data/livescores?date=yyyy-MM-dd (window today±3 enforced)
        app.MapGet("/data/livescores", ([FromServices] LiveScoresStore store, [FromQuery] string? date) =>
        {
            var center = ScraperConfig.TodayLocal();
            var min = center.AddDays(-3);
            var max = center.AddDays(+3);

            if (!DateOnly.TryParse(string.IsNullOrWhiteSpace(date) ? center.ToString("yyyy-MM-dd") : date, out var d))
                return Results.BadRequest(new { message = "invalid date", expectedFormat = "yyyy-MM-dd" });

            if (d < min || d > max)
                return Results.BadRequest(new
                {
                    message = "date outside allowed window (today±3)",
                    date = d.ToString("yyyy-MM-dd"),
                    allowed = new { from = min.ToString("yyyy-MM-dd"), to = max.ToString("yyyy-MM-dd") }
                });

            var key = d.ToString("yyyy-MM-dd");
            var day = store.Get(key);
            return day is null
                ? Results.NotFound(new { message = "No livescores for date (refresh first)", date = key })
                : Results.Json(day);
        });

        // POST /data/livescores/refresh
        app.MapPost("/data/livescores/refresh",
            async ([FromServices] LiveScoresScraperService svc) =>
        {
            var (refreshed, lastUpdatedUtc) = await svc.FetchAndStoreAsync();
            return Results.Json(new { ok = true, refreshed, lastUpdatedUtc });
        });

        // GET /data/livescores/download  (gz if accepted)
        app.MapGet("/data/livescores/download", (HttpContext ctx) =>
        {
            var jsonPath = LiveScoresFiles.File;
            var gzPath = jsonPath + ".gz";
            if (!System.IO.File.Exists(jsonPath))
                return Results.NotFound(new { message = "No livescores file yet" });

            var acceptsGzip = ctx.Request.Headers.AcceptEncoding.ToString()
                .Contains("gzip", StringComparison.OrdinalIgnoreCase);

            var pathToSend = (acceptsGzip && System.IO.File.Exists(gzPath)) ? gzPath : jsonPath;
            var fi = new FileInfo(pathToSend);

            ctx.Response.Headers["Vary"] = "Accept-Encoding";
            ctx.Response.Headers["X-File-Length"] = fi.Length.ToString();
            if (pathToSend.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers["Content-Encoding"] = "gzip";

            return Results.File(pathToSend, "application/json", enableRangeProcessing: true, fileDownloadName: "livescores.json");
        });

        // POST /data/livescores/cleanup?keepDays=7&dryRun=false
        app.MapPost("/data/livescores/cleanup",
            async ([FromServices] LiveScoresStore store,
                   [FromQuery] int keepDays = 7,
                   [FromQuery] bool dryRun = false) =>
        {
            var dates = store.Dates().ToList();
            var keep = dates.OrderByDescending(d => d).Take(Math.Max(1, keepDays)).ToList();

            var wouldRemove = dates.Count - keep.Count;
            if (dryRun) return Results.Json(new { ok = true, dryRun, wouldRemove, wouldKeep = keep.Count, keep });

            var removed = store.ShrinkTo(keep);
            await LiveScoresFiles.SaveAsync(store);
            return Results.Json(new { ok = true, removed, kept = keep.Count, keep });
        });

        return app;
    }
}
