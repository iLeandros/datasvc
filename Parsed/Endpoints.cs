// Parsed/Endpoints.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using DataSvc.Models;
using DataSvc.MainHelpers;
using DataSvc.Details;

namespace DataSvc.Parsed
{
    public static class ParsedEndpointMapping
    {
        public static IEndpointRouteBuilder MapParsedEndpoints(this IEndpointRouteBuilder app)
        {
            // ----- Back-compat shortcuts (public read) -----
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

            // ----- Date readers (public) -----
            // FIX M7: validate date input before parsing
            app.MapGet("/data/parsed/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                if (!DateOnly.TryParse(date, out var d))
                    return Results.BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd." });

                return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
                    ? Results.Ok(snap.Payload.TableDataGroup)
                    : Results.NotFound(new { error = "snapshot not found; refresh first", date });
            });

            app.MapGet("/data/html/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                if (!DateOnly.TryParse(date, out var d))
                    return Results.BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd." });

                return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
                    ? Results.Text(snap.Payload.HtmlContent ?? "", "text/html")
                    : Results.NotFound(new { error = "snapshot not found; refresh first", date });
            });

            app.MapGet("/data/snapshot/date/{date}", (string date, SnapshotPerDateStore perDateStore) =>
            {
                if (!DateOnly.TryParse(date, out var d))
                    return Results.BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd." });

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

            // ----- Status (public) -----
            app.MapGet("/data/perdate/status", (SnapshotPerDateStore perDateStore, DetailsStore detailsStore) =>
            {
                var center = ScraperConfig.TodayLocal();

                var dir = Path.GetDirectoryName(ScraperConfig.SnapshotPath(center))!;
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .OrderBy(x => x)
                        .ToList()
                    : new List<string>();

                var memDates = new List<string>();
                foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                    if (perDateStore.TryGet(d, out _))
                        memDates.Add(d.ToString("yyyy-MM-dd"));

                var stats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                {
                    var key = d.ToString("yyyy-MM-dd");
                    int parsedCount = 0, detailsCount = 0;
                    bool file = files.Contains(key, StringComparer.OrdinalIgnoreCase);

                    if (perDateStore.TryGet(d, out var snap) && snap.Payload?.TableDataGroup is not null)
                    {
                        var hrefs = snap.Payload.TableDataGroup
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
                    memoryDates = memDates,
                    diskFiles = files,
                    stats
                });
            });

            // ----- Admin / mutation endpoints — require authentication -----
            // FIX C2: cleanup, refresh routes require authorization

            app.MapPost("/data/parsed/cleanup", (
                [FromServices] ResultStore store,
                [FromQuery] bool clear = false,
                [FromQuery] bool deleteFile = false) =>
            {
                int deletedFiles = 0;

                if (clear)
                {
                    var snap = new DataSnapshot(DateTimeOffset.UtcNow, false, null, "cleared");
                    store.Set(snap);
                }

                if (deleteFile && System.IO.File.Exists(DataFiles.File))
                {
                    System.IO.File.Delete(DataFiles.File);
                    deletedFiles = 1;
                }

                return Results.Json(new { ok = true, cleared = clear, deletedFiles });
            }).RequireAuthorization();

            app.MapGet("/data/refresh-date/{date}", async (
                string date,
                int? hour,
                SnapshotPerDateStore perDateStore,
                IConfiguration cfg,
                ParsedTipsService tips,
                CancellationToken ct) =>
            {
                // FIX M7: validate date before use
                if (!DateOnly.TryParse(date, out var d))
                    return Results.BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd." });

                try
                {
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
                    return Results.Problem(title: "Fetch failed", detail: ex.Message,
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }).RequireAuthorization();

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
                var back = daysBack ?? 3;
                var ahead = daysAhead ?? 3;

                var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
                    store: perDateStore, cfg: cfg, tips: tips,
                    hourUtc: hour, center: center, back: back, ahead: ahead, ct: ct);

                BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

                return Results.Ok(new
                {
                    center = center.ToString("yyyy-MM-dd"),
                    back, ahead, refreshed, errors,
                    ok = errors.Count == 0
                });
            }).RequireAuthorization();

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
                var back = daysBack ?? 3;
                var ahead = daysAhead ?? 3;

                var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
                    store: perDateStore, cfg: cfg, tips: tips,
                    hourUtc: hour, center: center, back: back, ahead: ahead, ct: ct);

                BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

                return Results.Ok(new
                {
                    center = center.ToString("yyyy-MM-dd"),
                    back, ahead, refreshed, errors,
                    ok = errors.Count == 0
                });
            }).RequireAuthorization();

            return app;
        }
    }
}
