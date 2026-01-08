// Parsed/Endpoints.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc; // [FromServices], [FromQuery]
using System.IO;                // Path, Directory

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


namespace DataSvc.Parsed
{
    public static class ParsedEndpointMapping
    {
        public static IEndpointRouteBuilder MapParsedEndpoints(this IEndpointRouteBuilder app)
        {
            // ----- Back-compat shortcuts (resolve to Brussels "today") -----
            app.MapGet("/data/parsed", () =>
            {
                var d = ScraperConfig.TodayLocal();
                return Results.Redirect($"/data/parsed/date/{d:yyyy-MM-dd}");
            });

            app.MapGet("/data/html", () =>
            {
                var d = ScraperConfig.TodayLocal();
                return Results.Redirect($"/data/html/date/{d:yyyy-MM-dd}");
            });

            app.MapGet("/data/snapshot", () =>
            {
                var d = ScraperConfig.TodayLocal();
                return Results.Redirect($"/data/snapshot/date/{d:yyyy-MM-dd}");
            });

            // ----- Cleanup -----
            app.MapPost("/data/parsed/cleanup",
                ([FromServices] ResultStore store,
                 [FromQuery] bool clear = false,
                 [FromQuery] bool deleteFile = false) =>
            {
                int deletedFiles = 0;
            
                if (clear)
                {
                    // set an empty snapshot (Payload null); /data/parsed will return 404 afterwards
                    var snap = new DataSnapshot(DateTimeOffset.UtcNow, false, null, "cleared");
                    store.Set(snap);
                }
            
                if (deleteFile && System.IO.File.Exists(DataFiles.File))
                {
                    System.IO.File.Delete(DataFiles.File);
                    deletedFiles = 1;
                }
            
                return Results.Json(new
                {
                    ok = true,
                    cleared = clear,
                    deletedFiles
                });
            });

            // ----- Readers (by date) -----
            app.MapGet("/data/parsed/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                var d = DateOnly.Parse(date);
                return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
                    ? Results.Ok(snap.Payload.TableDataGroup)
                    : Results.NotFound(new { error = "snapshot not found; refresh first", date });
            });

            app.MapGet("/data/html/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                var d = DateOnly.Parse(date);
                return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
                    ? Results.Text(snap.Payload.HtmlContent ?? "", "text/html")
                    : Results.NotFound(new { error = "snapshot not found; refresh first", date });
            });

            app.MapGet("/data/snapshot/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                var d = DateOnly.Parse(date);
                if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
                    return Results.NotFound(new { error = "snapshot not found; refresh first", date });

                return Results.Ok(new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    lastUpdatedUtc = snap.LastUpdatedUtc,
                    tableDataGroup = snap.Payload.TableDataGroup,
                    titlesAndHrefs = snap.Payload.TitlesAndHrefs
                });
            });

            // ----- Refresh (single date) -----
            // Example: /data/refresh-date/2025-10-01?hour=14
            app.MapGet("/data/refresh-date/{date}", async (
                string date,
                int? hour,
                SnapshotPerDateStore perDateStore,
                IConfiguration cfg,
                ParsedTipsService tips,
                CancellationToken ct) =>
            {
                try
                {
                    var d = DateOnly.Parse(date);

                    // Fetch → enrich (tips) → store
                    var snap = await ScraperService.FetchOneDateAsync(d, cfg, hour, ct);
                    if (snap.Payload?.TableDataGroup is { } groups && groups.Count > 0)
                        await tips.ApplyTipsForDate(d, groups, ct);

                    perDateStore.Set(d, snap);

                    return Results.Ok(new
                    {
                        date = d.ToString("yyyy-MM-dd"),
                        hour = hour ?? DateTime.UtcNow.Hour,
                        lastUpdatedUtc = snap.LastUpdatedUtc
                    });
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "Fetch failed",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status502BadGateway);
                }
            });

            // ----- Refresh (window) -----
            // POST /data/refresh-window?date=YYYY-MM-DD&daysBack=3&daysAhead=3&hour=14
            app.MapPost("/data/refresh-window", async (
                string? date,
                int? daysBack,
                int? daysAhead,
                int? hour,
                SnapshotPerDateStore perDateStore,
                IConfiguration cfg,
                ParsedTipsService tips,
                CancellationToken ct) =>
            {
                var center = date is null ? ScraperConfig.TodayLocal() : DateOnly.Parse(date);
                var back   = daysBack  ?? 3;
                var ahead  = daysAhead ?? 3;

                var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
                    store:  perDateStore,
                    cfg:    cfg,
                    tips:   tips,
                    hourUtc: hour,
                    center: center,
                    back:   back,
                    ahead:  ahead,
                    ct:     ct);

                BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

                return Results.Ok(new
                {
                    center = center.ToString("yyyy-MM-dd"),
                    back, ahead,
                    refreshed,
                    errors,
                    ok = errors.Count == 0
                });
            });

            // GET /data/refresh-window?date=YYYY-MM-DD&daysBack=3&daysAhead=3&hour=14
            app.MapGet("/data/refresh-window", async (
                string? date,
                int? daysBack,
                int? daysAhead,
                int? hour,
                SnapshotPerDateStore perDateStore,
                IConfiguration cfg,
                ParsedTipsService tips,
                CancellationToken ct) =>
            {
                var center = date is null ? ScraperConfig.TodayLocal() : DateOnly.Parse(date);
                var back   = daysBack  ?? 3;
                var ahead  = daysAhead ?? 3;

                var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
                    store: perDateStore,
                    cfg:   cfg,
                    tips:  tips,
                    hourUtc: hour,
                    center: center,
                    back:   back,
                    ahead:  ahead,
                    ct:     ct);

                BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

                return Results.Ok(new
                {
                    center = center.ToString("yyyy-MM-dd"),
                    back, ahead,
                    refreshed,
                    errors,
                    ok = errors.Count == 0
                });
            });

            // ----- Status (RAM/disk + parsed vs details counts) -----
            app.MapGet("/data/perdate/status", (SnapshotPerDateStore perDateStore, DetailsStore detailsStore) =>
            {
                var center = ScraperConfig.TodayLocal();

                // disk files present
                var dir = Path.GetDirectoryName(ScraperConfig.SnapshotPath(center))!;
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .OrderBy(x => x)
                        .ToList()
                    : new List<string>();

                // dates in memory (today±3)
                var memDates = new List<string>();
                foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                    if (perDateStore.TryGet(d, out _))
                        memDates.Add(d.ToString("yyyy-MM-dd"));

                // per-date stats (parsed vs details, + file presence)
                var stats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                {
                    var key = d.ToString("yyyy-MM-dd");

                    int parsedCount = 0;
                    int detailsCount = 0;
                    bool file = files.Contains(key, StringComparer.OrdinalIgnoreCase);

                    if (perDateStore.TryGet(d, out var snap) && snap.Payload?.TableDataGroup is not null)
                    {
                        var hrefs = snap.Payload!.TableDataGroup
                            .SelectMany(g => g.Items)
                            .Select(i => i.Href)
                            .Where(h => !string.IsNullOrWhiteSpace(h))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        parsedCount = hrefs.Length;
                        detailsCount = hrefs.Count(h => detailsStore.Get(h) is not null);
                    }

                    stats[key] = new { parsed = parsedCount, details = detailsCount, file };
                }

                return Results.Ok(new
                {
                    tz = ScraperConfig.TimeZone,
                    center = center.ToString("yyyy-MM-dd"),
                    memoryDates = memDates, // in RAM
                    diskFiles = files,      // on disk
                    stats                   // parsed vs details counts + file presence
                });
            });

            return app;
        }
    }
}
