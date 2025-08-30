using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Compression + CORS
//builder.Services.AddResponseCompression();
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// App services
builder.Services.AddSingleton<ResultStore>();
builder.Services.AddSingleton<ScraperService>();
builder.Services.AddHostedService<RefreshJob>();

builder.Services.AddSingleton<DetailsStore>();
builder.Services.AddSingleton<DetailsScraperService>();
builder.Services.AddHostedService<DetailsRefreshJob>();
// Program.cs (server)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = null;       // keep PascalCase
    o.SerializerOptions.DictionaryKeyPolicy = null;        // (optional)
});


var app = builder.Build();
app.UseResponseCompression();
app.UseCors();
// ---------- API ----------
app.MapGet("/", () => Results.Redirect("/data/status"));

app.MapGet("/data/status", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null) return Results.Json(new { ready = false, message = "No data yet. Initial refresh pending." });
    return Results.Json(new { ready = s.Ready, s.LastUpdatedUtc, s.Error });
});

// /data/titles -> titles/hrefs only
app.MapGet("/data/titles", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null) return Results.NotFound(new { message = "No data yet" });
    return Results.Json(s.Payload.TitlesAndHrefs);
});

// /data/parsed -> groups with metadata + items (DTO)
app.MapGet("/data/parsed", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null)
        return Results.NotFound(new { message = "No data yet" });

    var groups = s.Payload.TableDataGroup ?? new ObservableCollection<TableDataGroup>();
    return Results.Json(groups); // no projection needed
});


// Raw HTML snapshot of the main page
app.MapGet("/data/html", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null) return Results.NotFound(new { message = "No data yet" });
    return Results.Text(s.Payload.HtmlContent ?? "", "text/html; charset=utf-8");
});

// Saved JSON snapshot (full)
app.MapGet("/data/snapshot", () => Results.File("/var/lib/datasvc/latest.json", "application/json"));

// Manual refresh (main page)
app.MapPost("/data/refresh", async ([FromServices] ScraperService svc) =>
{
    var snap = await svc.FetchAndStoreAsync();
    return Results.Json(new { ok = snap.Ready, snap.LastUpdatedUtc, snap.Error });
});

// -------- Details API --------
app.MapGet("/data/details/status", ([FromServices] DetailsStore store) =>
{
    var idx = store.Index();
    return Results.Json(new { total = idx.Count, lastSavedUtc = store.LastSavedUtc });
});

app.MapGet("/data/details/index", ([FromServices] DetailsStore store) =>
{
    var idx = store.Index()
        .Select(x => new { href = x.href, lastUpdatedUtc = x.lastUpdatedUtc })  // <- properties, not fields
        .OrderByDescending(x => x.lastUpdatedUtc)
        .ToList();

    return Results.Json(idx);
});


// ?href=...
// Raw details file (with Content-Length) for progress-friendly downloads
app.MapGet("/data/details/download", () =>
{
    var path = DetailsFiles.File; // "/var/lib/datasvc/details.json"
    if (!System.IO.File.Exists(path))
        return Results.NotFound(new { message = "No details file yet" });

    // enableRangeProcessing lets clients resume; content-length is set automatically
    return Results.File(path, "application/json", enableRangeProcessing: true);
});

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
app.MapPost("/data/details/cleanup",
    async ([FromServices] ResultStore root,
           [FromServices] DetailsStore store,
           [FromQuery] bool clear = false,
           [FromQuery] bool pruneFromParsed = false,
           [FromQuery] int? olderThanMinutes = null,
           [FromQuery] bool dryRun = false) =>
{
    // snapshot current state
    var (items, now) = store.Export();  // gives you the list + "now" timestamp
    int before = items.Count;

    if (clear)
    {
        if (dryRun) return Results.Json(new { ok = true, dryRun, wouldRemove = before, wouldKeep = 0 });
        store.Import(Array.Empty<DetailsRecord>()); // clears map via Import()
        await DetailsFiles.SaveAsync(store);
        return Results.Json(new { ok = true, removed = before, kept = 0 });
    }

    // Build "kept" set
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

    // Apply & persist
    store.Import(keptList);              // Import() clears and re-adds items
    await DetailsFiles.SaveAsync(store); // persist to /var/lib/datasvc/details.json

    return Results.Json(new { ok = true, removed, kept = keptList.Count });
});

app.MapGet("/data/details", ([FromServices] DetailsStore store, [FromQuery] string href) =>
{
    if (string.IsNullOrWhiteSpace(href))
        return Results.BadRequest(new { message = "href is required" });

    var rec = store.Get(href); // store.Get() normalizes internally
    if (rec is null)
        return Results.NotFound(new { message = "No details for href (yet)", normalized = DetailsStore.Normalize(href) });

    return Results.Json(rec.Payload);
});
// All details for all hrefs (keyed by normalized href)
// Toggles you already have:
//   ?teamsInfo=html        -> return original teamsinfo HTML
//   ?matchBetween=html     -> return original matchbtwteams HTML
// New toggle for this parser:
//   ?betStats=html         -> return original teamsbetstatistics HTML (omit parsed barCharts)
app.MapGet("/data/details/allhrefs",
    ([FromServices] DetailsStore store,
     [FromQuery] string? teamsInfo,
     [FromQuery] string? matchBetween,
	 [FromQuery] string? separateMatches,
     [FromQuery] string? betStats,
	 [FromQuery] string? facts,
	 [FromQuery] string? lastTeamsMatches,
	 [FromQuery] string? teamsStatistics,
	 [FromQuery] string? teamStandings) => // NEW
{
    bool preferTeamsInfoHtml    = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
    bool preferMatchBetweenHtml = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
    bool preferBetStatsHtml     = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
	bool preferFactsHtml        = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase); // <— NEW
	bool preferLastTeamsHtml    = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
	bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
	bool preferTeamsStatisticsHtml  = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase); // NEW
	bool preferTeamStandingsHtml = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase); // NEW
	
    var (items, generatedUtc) = store.Export();

    var byHref = items
        .OrderByDescending(i => i.LastUpdatedUtc)
        .ToDictionary(
            i => i.Href,
            i =>
            {
                // If you've already added these helpers earlier, keep using them
                var parsedTeamsInfo = preferTeamsInfoHtml ? null : TeamsInfoParser.Parse(i.Payload.TeamsInfoHtml);

                var matchDataBetween = preferMatchBetweenHtml
                    ? null
                    : MatchBetweenHelper.GetMatchDataBetween(i.Payload.MatchBetweenHtml ?? string.Empty);

				// NEW: parse the per-team recent matches (your new helper)
				var recentMatchesSeparate = preferSeparateMatchesHtml
				    ? null
				    : MatchSeparatelyHelper.GetMatchDataSeparately(
				          i.Payload.TeamMatchesSeparateHtml ?? string.Empty);


                // NEW: parse barcharts from teamsBetStatisticsHtml (unless HTML is preferred)
                //var barCharts = preferBetStatsHtml
                //    ? null
                //    : BarChartsParser.GetBarChartsData(i.Payload.TeamsBetStatisticsHtml ?? string.Empty);

				var rawBarCharts = preferBetStatsHtml ? null
				    : BarChartsParser.GetBarChartsData(i.Payload.TeamsBetStatisticsHtml ?? string.Empty);
				
				var barCharts = rawBarCharts?.Select(b => new {
				    title = b.Title,
				    halfContainerId = b.HalfContainerId,
				    items = b.ToList() // materialize the MatchFactData entries
				}).ToList();


				// NEW: facts (typed list or raw HTML)
	            var matchFacts = preferFactsHtml
	                ? null
	                : MatchFactsParser.GetMatchFacts(i.Payload.FactsHtml);

				// BEFORE
				// var lastTeamsWinrate = preferLastTeamsHtml 
				//     ? null
				//     : LastTeamsMatchesHelper.GetQuickTableWinratePercentagesFromSeperateTeams(i.Payload.LastTeamsMatchesHtml ?? string.Empty);
				
				// AFTER (safe for System.Text.Json)
				object? lastTeamsWinrate = null;
				if (!preferLastTeamsHtml)
				{
				    var m = LastTeamsMatchesHelper.GetQuickTableWinratePercentagesFromSeperateTeams(
				                i.Payload.LastTeamsMatchesHtml ?? string.Empty);
				
				    lastTeamsWinrate = new
				    {
				        wins   = new[] { m[0,0], m[0,1] },
				        draws  = new[] { m[1,0], m[1,1] },
				        losses = new[] { m[2,0], m[2,1] }
				    };
				}

				var teamsStats = preferTeamsStatisticsHtml
				    ? null
				    : GetTeamStatisticsHelper.GetTeamsStatistics(i.Payload.TeamsStatisticsHtml ?? string.Empty);

				var teamStandingsParsed = preferTeamStandingsHtml
	                ? null
	                : TeamStandingsHelper.GetTeamStandings(i.Payload.TeamStandingsHtml ?? string.Empty); // NEW
				
                return new
                {
                    href           = i.Href,
                    lastUpdatedUtc = i.LastUpdatedUtc,

                    // teams info
                    teamsInfo      = parsedTeamsInfo,
                    teamsInfoHtml  = preferTeamsInfoHtml ? i.Payload.TeamsInfoHtml : null,

                    // matches between
                    matchDataBetween = matchDataBetween,
                    matchBetweenHtml = preferMatchBetweenHtml ? i.Payload.MatchBetweenHtml : null,

					recentMatchesSeparate      = recentMatchesSeparate, // NEW parsed object
					recentMatchesSeparateHtml  = preferSeparateMatchesHtml ? i.Payload.TeamMatchesSeparateHtml : null,


                    // NEW: bar charts parsed from teamsbetstatistics
                    barCharts             = barCharts,
                    teamsBetStatisticsHtml= preferBetStatsHtml ? i.Payload.TeamsBetStatisticsHtml : null,

					// NEW: facts
	                matchFacts = matchFacts,
	                factsHtml  = preferFactsHtml ? i.Payload.FactsHtml : null,

					lastTeamsWinrate       = lastTeamsWinrate,                  // NEW (3x2 matrix: [W,D,L] x [team1,team2])
					lastTeamsMatchesHtml   = preferLastTeamsHtml ? i.Payload.LastTeamsMatchesHtml : null,

                    // NEW: team statistics (typed or raw)
				    teamsStatistics     = teamsStats,
				    teamsStatisticsHtml = preferTeamsStatisticsHtml ? i.Payload.TeamsStatisticsHtml : null,

					teamStandings     = teamStandingsParsed,
                	teamStandingsHtml = preferTeamStandingsHtml ? i.Payload.TeamStandingsHtml : null
                };
            },
            StringComparer.OrdinalIgnoreCase
        );

    return Results.Json(new
    {
        total        = byHref.Count,
        lastSavedUtc = store.LastSavedUtc,
        generatedUtc,
        items        = byHref
    });
});

// Optional: refresh then return the aggregated payload in one call
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
            href                   = i.Href,  // include href in each object
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

app.MapGet("/data/details/has", ([FromServices] DetailsStore store, [FromQuery] string href) =>
{
    if (string.IsNullOrWhiteSpace(href))
        return Results.BadRequest(new { message = "href is required" });

    var norm = DetailsStore.Normalize(href);
    var present = store.Index().Any(x => string.Equals(x.href, norm, StringComparison.OrdinalIgnoreCase));
    return Results.Json(new { normalized = norm, present });
});

// List first 50 cache keys we currently have in memory
app.MapGet("/data/details/debug/keys", ([FromServices] DetailsStore store) =>
{
    var keys = store.Index()
                    .Select(x => x.href)
                    .Take(50)
                    .ToList();
    return Results.Json(keys);
});

// Manual refresh (details for all current items)
app.MapPost("/data/details/refresh", async ([FromServices] DetailsScraperService svc) =>
{
    var result = await svc.RefreshAllFromCurrentAsync();
    return Results.Json(new { refreshed = result.Refreshed, skipped = result.Skipped, errors = result.Errors.Count, result.LastUpdatedUtc });
});
// Returns the first href currently in memory so we can copy/paste it
// Quick ping to see if the main scraper produced hrefs right now
app.MapGet("/data/first-href", ([FromServices] ResultStore store) =>
{
    var href = store.Current?.Payload?.TableDataGroup?
        .SelectMany(g => g.Items)
        .Select(i => i.Href)
        .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
    return Results.Json(new { href });
});

// Scrape ONE details page on-demand (doesn't require the cache to be warm)
app.MapGet("/data/details/fetch", async ([FromQuery] string href) =>
{
    if (string.IsNullOrWhiteSpace(href))
        return Results.BadRequest(new { message = "href is required" });

    var rec = await DetailsScraperService.FetchOneAsync(href);
    return Results.Json(rec.Payload);
});

// Fetch ONE details page and STORE it (also save to disk)
app.MapPost("/data/details/fetch-and-store", async ([FromServices] DetailsStore store, [FromQuery] string href) =>
{
    if (string.IsNullOrWhiteSpace(href))
        return Results.BadRequest(new { message = "href is required" });

    var rec = await DetailsScraperService.FetchOneAsync(href);
    store.Set(rec);                          // put into the in-memory map
    await DetailsFiles.SaveAsync(store);     // persist to /var/lib/datasvc/details.json
    return Results.Json(new { ok = true, href = rec.Href });
});

app.Run();

// ---------- Models & storage ----------
public sealed class ResultStore
{
    private readonly object _gate = new();
    private DataSnapshot? _current;
    public DataSnapshot? Current { get { lock (_gate) return _current; } }
    public void Set(DataSnapshot snap) { lock (_gate) _current = snap; }
}

public record DataSnapshot(DateTimeOffset LastUpdatedUtc, bool Ready, DataPayload? Payload, string? Error);

public record DataPayload(
    string HtmlContent,
    System.Collections.ObjectModel.ObservableCollection<TitlesAndHrefs> TitlesAndHrefs,
    System.Collections.ObjectModel.ObservableCollection<TableDataGroup> TableDataGroup
);


public static class DataFiles
{
    public const string Dir  = "/var/lib/datasvc";
    public const string File = "/var/lib/datasvc/latest.json";

    public static async Task SaveAsync(DataSnapshot snap)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
        var tmp = File + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, File, overwrite: true);
    }

    public static async Task<DataSnapshot?> LoadAsync()
    {
        if (!System.IO.File.Exists(File)) return null;
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(File);
            return JsonSerializer.Deserialize<DataSnapshot>(json);
        }
        catch { return null; }
    }
}

// ---------- Background job ----------
public sealed class ScraperService
{
    private readonly ResultStore _store;
    public ScraperService( [FromServices] ResultStore store ) => _store = store;

    public async Task<DataSnapshot> FetchAndStoreAsync(CancellationToken ct = default)
    {
        try
        {
            var html   = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo();
            var titles = GetStartupMainTitlesAndHrefs2024.GetStartupMainTitlesAndHrefs(html);
            var table  = GetStartupMainTableDataGroup2024.GetStartupMainTableDataGroup(html);

            var payload = new DataPayload(html, titles, table);
            var snap = new DataSnapshot(DateTimeOffset.UtcNow, true, payload, null);
            _store.Set(snap);
            await DataFiles.SaveAsync(snap);
            return snap;
        }
        catch (Exception ex)
        {
            var last = _store.Current;
            var snap = new DataSnapshot(DateTimeOffset.UtcNow, last?.Ready ?? false, last?.Payload, ex.Message);
            _store.Set(snap);
            await DataFiles.SaveAsync(snap);
            return snap;
        }
    }
}

public sealed class RefreshJob : BackgroundService
{
    private readonly ScraperService _svc;
    private readonly ResultStore _store;
    public RefreshJob(ScraperService svc, ResultStore store) { _svc = svc; _store = store; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prev = await DataFiles.LoadAsync();
        if (prev is not null) _store.Set(prev);

        await _svc.FetchAndStoreAsync(stoppingToken);
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await _svc.FetchAndStoreAsync(stoppingToken); }
        catch (OperationCanceledException) { }
    }
}

public class GetStartupMainPageFullInfo2024
{
    static readonly CookieContainer Cookies = new();
    static readonly HttpClient http = new(new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = Cookies
    }) { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<string> GetStartupMainPageFullInfo(string? url = null)
    {
        url ??= Environment.GetEnvironmentVariable("DATA_SOURCE_URL")
                ?? "https://www.statarea.com/predictions/date/2025-01-25/competition";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        req.Headers.Referrer = new Uri("https://www.statarea.com/");

        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }
}

public static class GetStartupMainTitlesAndHrefs2024
{
    public static ObservableCollection<TitlesAndHrefs> GetStartupMainTitlesAndHrefs(string htmlContent)
    {
        try
        {
            var website = new HtmlDocument();
            website.LoadHtml(htmlContent);

            var titlesAndhrefs = new ObservableCollection<TitlesAndHrefs>();
            var topbar = website.DocumentNode.Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "navigator")?
                .Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "buttons")?
                .Elements("a")
                .ToList();

            if (topbar != null)
            {
                foreach (var item in topbar)
                {
                    var titleAndHref = new TitlesAndHrefs
			{
			    Dates = item.InnerText,
			    Href  = item.Attributes["href"].Value
			};

                    titlesAndhrefs.Add(titleAndHref);
                }
            }

            return titlesAndhrefs;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetStartupMainTitlesAndHrefs2024 error: " + ex.Message);
            throw new Exception("Couldn't get GetStartupMainTitlesAndHrefs2024", ex.InnerException);
        }
    }
}

public static class GetStartupMainTableDataGroup2024
{
    public static ObservableCollection<TableDataGroup> GetStartupMainTableDataGroup(string htmlContent)
    {
        try
        {
            var website = new HtmlDocument();
            website.LoadHtml(htmlContent);

            var tableDataGroup = new ObservableCollection<TableDataGroup>();

            var matchesGroups = website.DocumentNode.Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "datacotainer full")?
                .Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "predictions")
                .Skip(1)
                .FirstOrDefault()?
                .Elements("div")
                .Where(o => o.Attributes["id"] != null);

            if (matchesGroups != null)
            {
                foreach (var group in matchesGroups)
                {
                    var items = new ObservableCollection<TableDataItem>();

                    var body = group.Descendants("div")
                        .FirstOrDefault(o => o.GetAttributeValue("class", "") == "body");

                    var matchesItems = body?.Elements("div")
                        .Where(o => o.GetAttributeValue("class", "") == "match");

                    if (matchesItems != null)
                    {
                        foreach (var matchItem in matchesItems)
                        {
                            var time = matchItem.Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "date")?.InnerText;

                            var teamone = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "teams")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "hostteam")?
                                .Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "name")?.InnerText;

                            var hrefs = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "teams")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "hostteam")?
                                .Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "name")?
                                .Element("a")?.Attributes["href"].Value;

                            if (hrefs != null) hrefs = hrefs.Replace(" ", "%20");

                            var teamonescore = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "teams")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "hostteam")?.FirstChild?.InnerText;

                            var teamtwo = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "teams")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "guestteam")?
                                .Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "name")?.InnerText;

                            var teamtwoscore = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "teams")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "guestteam")?.FirstChild?.InnerText;

                            var tip = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "tip")?
                                .Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "").Contains("value"));

                            var backgroundtipcolor = Colors.DarkSlateGray;
                            if (tip != null)
                            {
                                var tipClass = tip.Attributes["class"].Value;
                                if (tipClass == "value success") backgroundtipcolor = Colors.Green;
                                else if (tipClass == "value failed") backgroundtipcolor = Colors.Red;
                                else backgroundtipcolor = Colors.DarkSlateGray;
                            }

                            var likebutton = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "like");

                            var likepositive = likebutton?.Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "likepositive")?
                                .Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "value")?.InnerText;

                            var likenegative = likebutton?.Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "likenegative")?
                                .Elements("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "value")?.InnerText;

                            const string likebuttonimage = @"https://cdn0.iconfinder.com/data/icons/essentials-solid-glyphs-vol-1/100/Facebook-Like-Good-512.png";
                            const string dislikebuttonimage = @"https://cdn3.iconfinder.com/data/icons/wpzoom-developer-icon-set/500/139-512.png";

                            var likesandvotes = matchItem.Descendants("div")
                                .FirstOrDefault(p => p.GetAttributeValue("class", "") == "inforow")?
                                .FirstChild?
                                .Elements("div")
                                .Where(a => a.GetAttributeValue("class", "").Contains("coefbox")).ToList();

                            if (likesandvotes != null && likesandvotes.Count >= 11)
                            {
                                items.Add(new TableDataItem(
                                    "flag",
                                    backgroundtipcolor,
                                    time ?? "",
                                    renameTeam.renameTeamNameToFitDisplayLabel(teamone ?? ""),
                                    teamonescore,
                                    teamtwoscore,
                                    renameTeam.renameTeamNameToFitDisplayLabel(teamtwo ?? ""),
                                    tip?.InnerText,
                                    likebuttonimage,
                                    dislikebuttonimage,
                                    likepositive,
                                    likenegative,
                                    likesandvotes[0].InnerText,
                                    likesandvotes[1].InnerText,
                                    likesandvotes[2].InnerText,
                                    likesandvotes[3].InnerText,
                                    likesandvotes[4].InnerText,
                                    likesandvotes[5].InnerText,
                                    likesandvotes[6].InnerText,
                                    likesandvotes[7].InnerText,
                                    likesandvotes[8].InnerText,
                                    likesandvotes[9].InnerText,
                                    likesandvotes[10].InnerText,
                                    "Beta",
                                    hrefs,
                                    Colors.LightGray
                                ));
                            }
                        }

                        var groupImage = group.Descendants("img").FirstOrDefault()?.Attributes["src"].Value;
                        var groupName = group.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "name")?.InnerText.Trim();

                        if (groupImage != null && groupName != null)
                        {
                            tableDataGroup.Add(new TableDataGroup(groupImage, groupName, "TIP", items));
                        }
                    }
                }
            }

            return tableDataGroup;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetStartupMainTableDataGroup2024 error: " + ex.Message);
            throw new Exception("Couldn't get GetStartupMainTableDataGroup2024", ex);
        }
    }
}
// ---------- NEW: Details models, store, files, scraper, job ----------
public record DetailsPayload(
    string? TeamsInfoHtml,
    string? MatchBetweenHtml,
	string? TeamMatchesSeparateHtml, // NEW
    string? LastTeamsMatchesHtml,
    string? TeamsStatisticsHtml,
    string? TeamsBetStatisticsHtml,
	string? FactsHtml, // <— NEW (nullable for backward compat)
	string? TeamStandingsHtml // NEW
);

public record DetailsRecord(string Href, DateTimeOffset LastUpdatedUtc, DetailsPayload Payload);

public sealed class DetailsStore
{
    private readonly ConcurrentDictionary<string, DetailsRecord> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveGate = new();
    public DateTimeOffset? LastSavedUtc { get; private set; }

    public void Set(DetailsRecord rec) => _map[rec.Href] = rec;
    public DetailsRecord? Get(string href) => _map.TryGetValue(Normalize(href), out var rec) ? rec : null;

    public List<(string href, DateTimeOffset lastUpdatedUtc)> Index()
        => _map.Values.Select(v => (v.Href, v.LastUpdatedUtc)).ToList();

    public (IReadOnlyList<DetailsRecord> items, DateTimeOffset now) Export()
        => (_map.Values.ToList(), DateTimeOffset.UtcNow);

    public void Import(IEnumerable<DetailsRecord> items)
    {
        _map.Clear();
        foreach (var it in items) _map[it.Href] = it;
    }
	/// <summary>
    /// Remove any cached href that is NOT present in <paramref name="keep"/>.
    /// Returns the number of removed items.
    /// </summary>
    public int ShrinkTo(IReadOnlyCollection<string> keep)
    {
        var set = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var key in _map.Keys)
        {
            if (!set.Contains(key))
            {
                if (_map.TryRemove(key, out _)) removed++;
            }
        }
        return removed;
    }
    public void MarkSaved(DateTimeOffset ts) { lock (_saveGate) LastSavedUtc = ts; }

   public static string Normalize(string href)
{
    if (string.IsNullOrWhiteSpace(href)) return "";

    var s = WebUtility.HtmlDecode(href).Trim();

    // If it's already absolute, return the canonical AbsoluteUri.
    // This handles inputs like ".../Slough Town (England)/..." by encoding to %20.
    if (Uri.TryCreate(s, UriKind.Absolute, out var abs))
        return abs.AbsoluteUri;

    // Protocol-relative (//host/...)
    if (s.StartsWith("//")) return "https:" + s;

    // Host without scheme
    if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("statarea.com", StringComparison.OrdinalIgnoreCase))
        return "https://" + s.TrimStart('/');

    // Site-relative
    var baseUri = new Uri("https://www.statarea.com/");
    if (!s.StartsWith("/")) s = "/" + s;
    return new Uri(baseUri, s).AbsoluteUri; // canonicalize
}



}

public static class DetailsFiles
{
    public const string File = "/var/lib/datasvc/details.json";

    public static async Task SaveAsync( [FromServices] DetailsStore store )
    {
        var (items, now) = store.Export();
        var json = JsonSerializer.Serialize(new { lastSavedUtc = now, items }, new JsonSerializerOptions { WriteIndented = false });
        var tmp = File + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, File, overwrite: true);
        store.MarkSaved(now);
    }

    public static async Task<IReadOnlyList<DetailsRecord>> LoadAsync()
    {
        if (!System.IO.File.Exists(File)) return Array.Empty<DetailsRecord>();
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(File);
            var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items").Deserialize<List<DetailsRecord>>() ?? new();
            return items;
        }
        catch { return Array.Empty<DetailsRecord>(); }
    }
}

public sealed class DetailsScraperService
{
    private readonly ResultStore _root;
    private readonly DetailsStore _store;
	
	static int GetEnvInt(string name, int def)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? Math.Max(1, v) : def;

	readonly int _maxParallel     = GetEnvInt("DETAILS_PARALLEL", 16);   // was 4
	readonly int _timeoutSeconds  = GetEnvInt("DETAILS_TIMEOUT_SECONDS", 10); // was 30
	readonly TimeSpan _ttl        = TimeSpan.FromMinutes(GetEnvInt("DETAILS_TTL_MINUTES", 1)); // 3h default

    static readonly SocketsHttpHandler _handler = new()
	{
	    AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip | DecompressionMethods.Deflate,
	    AllowAutoRedirect = true,
	    MaxConnectionsPerServer = 100,
	    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
	    EnableMultipleHttp2Connections = true,
	};
	static readonly HttpClient http = new(_handler);

    public DetailsScraperService(ResultStore root, DetailsStore store)
    {
        _root = root;
        _store = store;
    }

    public sealed record RefreshSummary(
									    int Refreshed,
									    int Skipped,
									    int Deleted,
									    List<string> Errors,
									    DateTimeOffset LastUpdatedUtc
									);


    public async Task<RefreshSummary> RefreshAllFromCurrentAsync(CancellationToken ct = default)
	{
	    var current = _root.Current?.Payload?.TableDataGroup;
	    if (current is null)
	        return new RefreshSummary(0, 0, 0, new List<string>{ "No root payload yet" }, DateTimeOffset.UtcNow);
	
	    var hrefs = current.SelectMany(g => g.Items)
	                       .Select(i => i.Href)
	                       .Where(h => !string.IsNullOrWhiteSpace(h))
	                       .Select(DetailsStore.Normalize)
	                       .Distinct(StringComparer.OrdinalIgnoreCase)
	                       .ToList();
	
	    int refreshed = 0, skipped = 0;
	    var errors = new List<string>();
	    var sem = new SemaphoreSlim(_maxParallel);
	    var now = DateTimeOffset.UtcNow;
	
	    var tasks = hrefs.Select(async href =>
	    {
	        await sem.WaitAsync(ct);
	        try
	        {
	            var existing = _store.Get(href);
	            if (existing is not null && (now - existing.LastUpdatedUtc) < _ttl)
	            {
	                Interlocked.Increment(ref skipped);
	                return;
	            }
	
	            var rec = await FetchOneAsync(href, ct);
	            _store.Set(rec);
	            Interlocked.Increment(ref refreshed);
	        }
	        catch (Exception ex)
	        {
	            lock (errors) errors.Add($"{href}: {ex.Message}");
	        }
	        finally { sem.Release(); }
	    });
	
	    await Task.WhenAll(tasks);

		// (A) If we parsed 0 hrefs this tick, keep the existing cache intact.
		if (hrefs.Count == 0)
		{
		    errors.Add("Parsed 0 hrefs — skipped prune/save to avoid wiping cache.");
		    return new RefreshSummary(refreshed, skipped, 0, errors, DateTimeOffset.UtcNow);
		}
		
		// (B) If we had zero verified items (nothing refreshed and nothing valid by TTL),
		//     keep the cache instead of pruning.
		if ((refreshed + skipped) == 0 && _store.Index().Count > 0)
		{
		    errors.Add("No successes this tick — kept previous cache, skipping prune/save.");
		    return new RefreshSummary(refreshed, skipped, 0, errors, DateTimeOffset.UtcNow);
		}
		
		var deleted = _store.ShrinkTo(hrefs);
		
		// (C) If after all that we somehow have 0 items, avoid persisting an empty file.
		if (_store.Index().Count == 0)
		{
		    errors.Add("Store empty after refresh — not saving empty details.json.");
		    return new RefreshSummary(refreshed, skipped, deleted, errors, DateTimeOffset.UtcNow);
		}
		
		await DetailsFiles.SaveAsync(_store);
		return new RefreshSummary(refreshed, skipped, deleted, errors, DateTimeOffset.UtcNow);

	}
	
	public static async Task<DetailsRecord> FetchOneAsync(string href, CancellationToken ct = default)
	{
	    var abs = DetailsStore.Normalize(href);
	    Debug.WriteLine($"[details] fetching: {abs}");
	
	    // env-configurable per-request timeout (default 10s)
	    var timeoutSeconds = 10;
	    if (int.TryParse(Environment.GetEnvironmentVariable("DETAILS_TIMEOUT_SECONDS"), out var t) && t > 0)
	        timeoutSeconds = t;
	
	    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
	    linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
	
	    using var req = new HttpRequestMessage(HttpMethod.Get, abs)
	    {
	        Version = HttpVersion.Version20,
	        VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
	    };
	    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
	    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
	    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	    req.Headers.Referrer = new Uri("https://www.statarea.com/");
	
	    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
	    res.EnsureSuccessStatusCode();
	    var html = await res.Content.ReadAsStringAsync(linkedCts.Token);
	
	    // ---- parse the 5 sections from the fetched page ----
	    var doc = new HtmlAgilityPack.HtmlDocument();
	    doc.LoadHtml(html);
	
	    // Helper: pick the first div with an exact class token match
	    static string? SectionFirst(HtmlDocument d, string cls) =>
	        d.DocumentNode.SelectSingleNode($"//div[contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')]")?.OuterHtml;
	
	    // Helper: choose the *filled* matchbtwteams block (most rows / longest non-blank)
	    static string? SectionMatchBetweenFilled(HtmlDocument d, out int foundNodes, out int pickedRows)
	    {
	        var nodes = d.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' matchbtwteams ')]");
	        foundNodes = nodes?.Count ?? 0;
	        pickedRows = 0;
	        if (nodes is null || nodes.Count == 0) return null;
	
	        HtmlAgilityPack.HtmlNode? best = null;
	        var bestScore = int.MinValue;
	
	        foreach (var n in nodes)
	        {
	            var rows = n.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' matchitem ')]");
	            var rowCount = rows?.Count ?? 0;
	            var text  = HtmlEntity.DeEntitize(n.InnerText ?? string.Empty).Trim();
	            var len   = n.InnerHtml?.Length ?? 0;
	
	            // row count dominates; prefer longer; penalize blank/&nbsp;
	            var score = rowCount * 1_000_000 + len;
	            if (string.IsNullOrEmpty(text) || text == "&nbsp;") score -= 100_000_000;
	
	            if (score > bestScore)
	            {
	                bestScore = score;
	                best = n;
	                pickedRows = rowCount;
	            }
	        }
	        return best?.OuterHtml ?? nodes[0].OuterHtml;
	    }
		// Program.cs — replace SectionFactsWithRows with this version
		static string? SectionFactsWithRows(HtmlDocument d)
		{
		    // Case-insensitive token match helper via XPath translate()
		    const string ToLower = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
		    const string ToUpper = "abcdefghijklmnopqrstuvwxyz";
		    string tokenFacts = " contains(concat(' ', translate(normalize-space(@class), '" + ToLower + "', '" + ToUpper + "'), ' '), ' facts ') ";
		
		    // 1) Primary candidates: any DIV/SECTION with class contains 'facts' OR id contains 'facts' (case-insensitive)
		    var candidates = d.DocumentNode.SelectNodes(
		        "//*[self::div or self::section][" + tokenFacts + "] | " +
		        "//*[@id and contains(translate(@id,'" + ToLower + "','" + ToUpper + "'),'facts')]"
		    ) ?? new HtmlNodeCollection(null);
		
		    // 2) Anchor hint: the page uses #linkmatchfacts – grab the first facts-block that follows it
		    var anchor = d.DocumentNode.SelectSingleNode("//*[@name='linkmatchfacts' or @id='linkmatchfacts']");
		    if (anchor != null)
		    {
		        var near = anchor.SelectSingleNode(
		            "following::*[self::div or self::section][" + tokenFacts + "][1]"
		        );
		        if (near != null) candidates.Add(near);
		    }
		
		    // 3) If there are multiple blocks, prefer the one that looks "filled"
		    HtmlNode? best = null;
		    int bestScore = int.MinValue;
		
		    foreach (var n in candidates.Distinct())
		    {
		        // tolerate case differences for inner nodes too
		        var rows  = n.SelectNodes(".//*[contains(translate(@class,'" + ToLower + "','" + ToUpper + "'),'datarow')]")?.Count ?? 0;
		        var chart = n.SelectNodes(".//*[contains(translate(@class,'" + ToLower + "','" + ToUpper + "'),'stackedbarchart')]")?.Count ?? 0;
		        var len   = n.InnerHtml?.Length ?? 0;
		
		        // rows dominate, then presence of chart, then length
		        int score = rows * 1_000_000 + chart * 10_000 + len;
		        if (score > bestScore) { bestScore = score; best = n; }
		    }
		
		    // Debug aid (optional)
		    Debug.WriteLine($"[details] facts: candidates={candidates.Count}, pickedScore={bestScore}");
		
		    return best?.OuterHtml;
		}


	
	    // Use helpers
	    var teamsInfoHtml        = SectionFirst(doc, "teamsinfo");
	    var lastTeamsMatchesHtml = SectionFirst(doc, "lastteamsmatches");
	    var teamsStatisticsHtml  = SectionFirst(doc, "teamsstatistics");
	    var teamsBetStatsHtml    = SectionFirst(doc, "teamsbetstatistics");
		// NEW: the 6th div
		var factsHtml = SectionFactsWithRows(doc); // instead of SectionFirst(doc, "facts")

		var teamMatchesSeparateHtml = SectionFirst(doc, "lastteamsmatches"); // NEW
		
	    int mbDivs, mbRows;
	    var matchBetweenHtml = SectionMatchBetweenFilled(doc, out mbDivs, out mbRows);
	    Debug.WriteLine($"[details] matchbtwteams: found {mbDivs} block(s); picked block with {mbRows} row(s)");

		var teamStandingsHtml = SectionFirst(doc, "teamsstandings"); // NEW
		
	    var payload = new DetailsPayload(
	        TeamsInfoHtml:          teamsInfoHtml,
	        MatchBetweenHtml:       matchBetweenHtml,
			TeamMatchesSeparateHtml: teamMatchesSeparateHtml, // NEW
	        LastTeamsMatchesHtml:   lastTeamsMatchesHtml,
	        TeamsStatisticsHtml:    teamsStatisticsHtml,
	        TeamsBetStatisticsHtml: teamsBetStatsHtml,
			FactsHtml:              factsHtml,
			TeamStandingsHtml:    teamStandingsHtml // NEW
	    );
	
	    return new DetailsRecord(abs, DateTimeOffset.UtcNow, payload);
	}
}

public sealed class DetailsRefreshJob : BackgroundService
{
    private readonly DetailsScraperService _svc;
    private readonly DetailsStore _store;

    public DetailsRefreshJob(DetailsScraperService svc, DetailsStore store)
    {
        _svc = svc; _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load previous cache on startup
        var prev = await DetailsFiles.LoadAsync();
        if (prev.Count > 0) _store.Import(prev);

        // Let the main page warm up first
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }

        // Initial run (guarded)
        try
        {
            var r = await _svc.RefreshAllFromCurrentAsync(stoppingToken);
            Debug.WriteLine($"[details] initial refreshed={r.Refreshed} skipped={r.Skipped} errors={r.Errors.Count}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[details] initial run failed: {ex}");
        }

        // Every 5 minutes — never die on exceptions
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var r = await _svc.RefreshAllFromCurrentAsync(stoppingToken);
                    Debug.WriteLine($"[details] tick refreshed={r.Refreshed} skipped={r.Skipped} errors={r.Errors.Count}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[details] tick failed: {ex}");
                    // keep looping
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
