// Details/Endpoints.cs
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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

namespace DataSvc.Details;

public static class DetailsEndpointMapping
{
    public static IEndpointRouteBuilder MapDetailsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Status + Index ---------------------------------------------------
        app.MapGet("/data/details/status", ([FromServices] DetailsStore store) =>
        {
            var idx = store.Index();
            return Results.Json(new { total = idx.Count, lastSavedUtc = store.LastSavedUtc });
        });

        app.MapGet("/data/details/index", ([FromServices] DetailsStore store) =>
        {
            var idx = store.Index()
                .Select(x => new { href = x.href, lastUpdatedUtc = x.lastUpdatedUtc })
                .OrderByDescending(x => x.lastUpdatedUtc)
                .ToList();

            return Results.Json(idx);
        });

        // ---- Download current details.json (gz if accepted) -------------------
        app.MapGet("/data/details/download", (HttpContext ctx) =>
        {
            var jsonPath = DetailsFiles.File;           // e.g., /var/lib/datasvc/details.json
            var gzPath   = jsonPath + ".gz";
            if (!File.Exists(jsonPath))
                return Results.NotFound(new { message = "No details file yet" });

            var acceptsGzip = ctx.Request.Headers.AcceptEncoding.ToString()
                                   .Contains("gzip", StringComparison.OrdinalIgnoreCase);

            var pathToSend = (acceptsGzip && File.Exists(gzPath)) ? gzPath : jsonPath;
            var fi = new FileInfo(pathToSend);

            ctx.Response.Headers["Vary"] = "Accept-Encoding";
            ctx.Response.Headers["X-File-Length"] = fi.Length.ToString();
            if (pathToSend.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers["Content-Encoding"] = "gzip";

            return Results.File(pathToSend, "application/json", enableRangeProcessing: true, fileDownloadName: "details.json");
        });

        // ---- Cleanup (clear/prune) --------------------------------------------
        app.MapPost("/data/details/cleanup",
            async ([FromServices] ResultStore root,
                   [FromServices] DetailsStore store,
                   [FromQuery] bool clear = false,
                   [FromQuery] bool pruneFromParsed = false,
                   [FromQuery] int? olderThanMinutes = null,
                   [FromQuery] bool dryRun = false) =>
        {
            var (items, now) = store.Export();
            int before = items.Count;

            if (clear)
            {
                if (dryRun) return Results.Json(new { ok = true, dryRun, wouldRemove = before, wouldKeep = 0 });
                store.Import(Array.Empty<DetailsRecord>());
                await DetailsFiles.SaveAsync(store);
                return Results.Json(new { ok = true, removed = before, kept = 0 });
            }

            IEnumerable<DetailsRecord> kept = items;

            if (olderThanMinutes is int min && min >= 0)
            {
                var cutoff = now - TimeSpan.FromMinutes(min);
                kept = kept.Where(i => i.LastUpdatedUtc >= cutoff);
            }

            if (pruneFromParsed)
            {
                var hrefs = root.Current?.Payload?.TableDataGroup?
                    .SelectMany(g => g.Items)
                    .Select(i => i.Href)
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(DetailsStore.Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                kept = kept.Where(i => hrefs.Contains(i.Href));
            }

            var keptList = kept.ToList();
            int removed = before - keptList.Count;

            if (dryRun) return Results.Json(new { ok = true, dryRun, wouldRemove = removed, wouldKeep = keptList.Count });

            store.Import(keptList);
            await DetailsFiles.SaveAsync(store);

            return Results.Json(new { ok = true, removed, kept = keptList.Count });
        });

        // ---- Get one by href ---------------------------------------------------
        app.MapGet("/data/details", ([FromServices] DetailsStore store, [FromQuery] string href) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return Results.BadRequest(new { message = "href is required" });

            var rec = store.Get(href);
            if (rec is null)
                return Results.NotFound(new { message = "No details for href (yet)", normalized = DetailsStore.Normalize(href) });

            return Results.Json(rec.Payload);
        });

        // ---- All HREFs (toggle-able sections) ---------------------------------
        app.MapGet("/data/details/allhrefs",
            ([FromServices] DetailsStore store,
             [FromQuery] string? teamsInfo,
             [FromQuery] string? matchBetween,
             [FromQuery] string? separateMatches,
             [FromQuery] string? betStats,
             [FromQuery] string? facts,
             [FromQuery] string? lastTeamsMatches,
             [FromQuery] string? teamsStatistics,
             [FromQuery] string? teamStandings) =>
        {
            bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
            bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
            bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
            bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
            bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

            var (items, _) = store.Export();

            var byHref = items
                .OrderBy(i => i.Href, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    i => i.Href,
                    i => AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                            i,
                            preferTeamsInfoHtml,
                            preferMatchBetweenHtml,
                            preferSeparateMatchesHtml,
                            preferBetStatsHtml,
                            preferFactsHtml,
                            preferLastTeamsHtml,
                            preferTeamsStatisticsHtml,
                            preferTeamStandingsHtml),
                    StringComparer.OrdinalIgnoreCase
                );

            return Results.Json(new
            {
                total        = byHref.Count,
                lastSavedUtc = store.LastSavedUtc,
                items        = byHref
            });
        });

        // ---- All HREFs for a specific date (from parsed snapshot ordering) ----
        app.MapGet("/data/details/allhrefs/date/{date}",
        (
            string date,
            [FromServices] SnapshotPerDateStore perDateStore,
            [FromServices] DetailsStore store,
            [FromQuery] string? teamsInfo,
            [FromQuery] string? matchBetween,
            [FromQuery] string? separateMatches,
            [FromQuery] string? betStats,
            [FromQuery] string? facts,
            [FromQuery] string? lastTeamsMatches,
            [FromQuery] string? teamsStatistics,
            [FromQuery] string? teamStandings
        ) =>
        {
            var d = DateOnly.Parse(date);
            if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
                return Results.NotFound(new { message = "No parsed snapshot for date (refresh first)", date });

            var hrefs = snap.Payload.TableDataGroup?
                .SelectMany(g => g.Items)
                .Select(i => i.Href)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(DetailsStore.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var index = hrefs
                        .Select((h, i) => (h, i))
                        .ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);

            var records = hrefs
                        .Select(h => store.Get(h))
                        .Where(r => r is not null)
                        .Cast<DetailsRecord>()
                        .OrderBy(r => index[r.Href])
                        .ToList();

            bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
            bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
            bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
            bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
            bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

            var byHref = records.ToDictionary(
                i => i.Href,
                i => AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                        i,
                        preferTeamsInfoHtml,
                        preferMatchBetweenHtml,
                        preferSeparateMatchesHtml,
                        preferBetStatsHtml,
                        preferFactsHtml,
                        preferLastTeamsHtml,
                        preferTeamsStatisticsHtml,
                        preferTeamStandingsHtml),
                StringComparer.OrdinalIgnoreCase);

            var envelope = new
            {
                date = d.ToString("yyyy-MM-dd"),
                total = byHref.Count,
                items = byHref
            };

            _ = DetailsPerDateFiles.SaveAsync(d, envelope);
            return Results.Json(envelope);
        });

        // ---- Item (with optional fetch-if-missing) -----------------------------
        app.MapGet("/data/details/item",
        async (
            [FromServices] DetailsStore store,
            [FromQuery] string href,
            [FromQuery] string? teamsInfo,
            [FromQuery] string? matchBetween,
            [FromQuery] string? separateMatches,
            [FromQuery] string? betStats,
            [FromQuery] string? facts,
            [FromQuery] string? lastTeamsMatches,
            [FromQuery] string? teamsStatistics,
            [FromQuery] string? teamStandings,
            [FromQuery] bool fetchIfMissing = true
        ) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return Results.BadRequest(new { message = "href is required" });

            static bool PayloadLooksEmpty(DetailsPayload? p)
            {
                if (p is null) return true;
                return string.IsNullOrWhiteSpace(p.TeamsInfoHtml)
                    && string.IsNullOrWhiteSpace(p.MatchBetweenHtml)
                    && string.IsNullOrWhiteSpace(p.TeamMatchesSeparateHtml)
                    && string.IsNullOrWhiteSpace(p.LastTeamsMatchesHtml)
                    && string.IsNullOrWhiteSpace(p.TeamsStatisticsHtml)
                    && string.IsNullOrWhiteSpace(p.TeamsBetStatisticsHtml)
                    && string.IsNullOrWhiteSpace(p.FactsHtml)
                    && string.IsNullOrWhiteSpace(p.TeamStandingsHtml);
            }

            var rec = store.Get(href);

            if (fetchIfMissing && (rec is null || PayloadLooksEmpty(rec.Payload)))
            {
                try
                {
                    var existing = rec;
                    var fresh = await DetailsScraperService.FetchOneWithRetryAsync(href);
                    var merged = DetailsMerge.Merge(existing, fresh);
                    store.Set(merged);
                    await DetailsFiles.SaveAsync(store);
                    rec = merged;
                }
                catch (Exception ex)
                {
                    if (rec is null)
                        return Results.NotFound(new { message = "No details for href (yet)", normalized = DetailsStore.Normalize(href), error = ex.Message });
                }
            }

            if (rec is null)
                return Results.NotFound(new { message = "No details for href (yet)", normalized = DetailsStore.Normalize(href) });

            bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
            bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
            bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
            bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
            bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

            var item = AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                rec,
                preferTeamsInfoHtml,
                preferMatchBetweenHtml,
                preferSeparateMatchesHtml,
                preferBetStatsHtml,
                preferFactsHtml,
                preferLastTeamsHtml,
                preferTeamsStatisticsHtml,
                preferTeamStandingsHtml
            );

            return Results.Json(item);
        });

        // ---- Item by index (same ordering as /allhrefs) -----------------------
        app.MapGet("/data/details/item-by-index",
        (
            [FromServices] DetailsStore store,
            [FromQuery] int index,
            [FromQuery] string? teamsInfo,
            [FromQuery] string? matchBetween,
            [FromQuery] string? separateMatches,
            [FromQuery] string? betStats,
            [FromQuery] string? facts,
            [FromQuery] string? lastTeamsMatches,
            [FromQuery] string? teamsStatistics,
            [FromQuery] string? teamStandings
        ) =>
        {
            var list = store.Export().items
                .OrderByDescending(i => i.LastUpdatedUtc)
                .ToList();

            if (index < 0 || index >= list.Count)
                return Results.NotFound(new { message = "index out of range", index, total = list.Count });

            var rec = list[index];

            bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
            bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
            bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
            bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
            bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

            var item = AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                rec,
                preferTeamsInfoHtml,
                preferMatchBetweenHtml,
                preferSeparateMatchesHtml,
                preferBetStatsHtml,
                preferFactsHtml,
                preferLastTeamsHtml,
                preferTeamsStatisticsHtml,
                preferTeamStandingsHtml
            );

            return Results.Json(item);
        });

        // ---- POST /items (batch) ----------------------------------------------
        app.MapPost("/data/details/items",
        (
            [FromServices] DetailsStore store,
            [FromBody] string[] hrefs,
            [FromQuery] string? teamsInfo,
            [FromQuery] string? matchBetween,
            [FromQuery] string? separateMatches,
            [FromQuery] string? betStats,
            [FromQuery] string? facts,
            [FromQuery] string? lastTeamsMatches,
            [FromQuery] string? teamsStatistics,
            [FromQuery] string? teamStandings
        ) =>
        {
            var list = (hrefs ?? Array.Empty<string>())
                .Select(DetailsStore.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(h => store.Get(h))
                .Where(r => r is not null)
                .Cast<DetailsRecord>()
                .ToList();

            bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
            bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
            bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
            bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
            bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
            bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

            var dict = list.ToDictionary(
                r => r.Href,
                r => AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                        r,
                        preferTeamsInfoHtml,
                        preferMatchBetweenHtml,
                        preferSeparateMatchesHtml,
                        preferBetStatsHtml,
                        preferFactsHtml,
                        preferLastTeamsHtml,
                        preferTeamsStatisticsHtml,
                        preferTeamStandingsHtml),
                StringComparer.OrdinalIgnoreCase
            );

            return Results.Json(new { total = dict.Count, items = dict });
        });

        // ---- Refresh + return aggregate ---------------------------------------
        app.MapPost("/data/details/refresh-and-get",
            async ([FromServices] DetailsScraperService svc,
                   [FromServices] DetailsStore store) =>
        {
            await svc.RefreshAllFromCurrentAsync();

            var (items, generatedUtc) = store.Export();

            var byHref = items.ToDictionary(
                i => i.Href,
                i => new
                {
                    href                   = i.Href,
                    lastUpdatedUtc         = i.LastUpdatedUtc,
                    teamsInfoHtml          = i.Payload.TeamsInfoHtml,
                    matchBetweenHtml       = i.Payload.MatchBetweenHtml,
                    lastTeamsMatchesHtml   = i.Payload.LastTeamsMatchesHtml,
                    teamsStatisticsHtml    = i.Payload.TeamsStatisticsHtml,
                    teamsBetStatisticsHtml = i.Payload.TeamsBetStatisticsHtml
                },
                StringComparer.OrdinalIgnoreCase
            );

            return Results.Json(new { total = byHref.Count, generatedUtc, items = byHref });
        });

        // ---- Misc helpers ------------------------------------------------------
        app.MapGet("/data/details/has", ([FromServices] DetailsStore store, [FromQuery] string href) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return Results.BadRequest(new { message = "href is required" });

            var norm = DetailsStore.Normalize(href);
            var present = store.Index().Any(x => string.Equals(x.href, norm, StringComparison.OrdinalIgnoreCase));
            return Results.Json(new { normalized = norm, present });
        });

        app.MapGet("/data/details/debug/keys", ([FromServices] DetailsStore store) =>
        {
            var keys = store.Index().Select(x => x.href).Take(50).ToList();
            return Results.Json(keys);
        });

        // ---- Manual refresh & on-demand fetches --------------------------------
        app.MapPost("/data/details/refresh", async ([FromServices] DetailsScraperService svc) =>
        {
            var result = await svc.RefreshAllFromCurrentAsync();
            return Results.Json(new { refreshed = result.Refreshed, skipped = result.Skipped, errors = result.Errors.Count, result.LastUpdatedUtc });
        });

        app.MapGet("/data/details/fetch", async ([FromQuery] string href) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return Results.BadRequest(new { message = "href is required" });

            var rec = await DetailsScraperService.FetchOneAsync(href);
            return Results.Json(rec.Payload);
        });

        app.MapPost("/data/details/fetch-and-store", async ([FromServices] DetailsStore store, [FromQuery] string href) =>
        {
            if (string.IsNullOrWhiteSpace(href))
                return Results.BadRequest(new { message = "href is required" });

            var rec = await DetailsScraperService.FetchOneAsync(href);
            store.Set(rec);
            await DetailsFiles.SaveAsync(store);
            return Results.Json(new { ok = true, href = rec.Href });
        });

        return app;
    }
}
