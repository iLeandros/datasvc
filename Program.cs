using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
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
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http.Metadata;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using Microsoft.Maui.Graphics;
using Dapper;
using System.Linq;
using System.IO.Compression;
using Microsoft.AspNetCore.Authentication;
using DataSvc.Auth; // AuthController + SessionAuthHandler namespace
using DataSvc.MainHelpers; // MainHelpers
using DataSvc.Likes; // MainHelpers
using DataSvc.Services; // Services
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using Google.Apis.Auth;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// --- Auth & Controllers ---
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = SessionAuthHandler.Scheme;
    options.DefaultChallengeScheme = SessionAuthHandler.Scheme;
})
.AddScheme<AuthenticationSchemeOptions, SessionAuthHandler>(SessionAuthHandler.Scheme, _ => { });

builder.Services.AddAuthorization();

// Controllers (for AuthController). We return camelCase to match the MAUI client.
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Compression + CORS
//builder.Services.AddResponseCompression();
builder.Services.Configure<GzipCompressionProviderOptions>(p =>
{
    p.Level = System.IO.Compression.CompressionLevel.Fastest;
});
/*
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
*/
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// add next to your existing ResponseCompression config
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json", "application/x-ndjson", "text/event-stream" });
});

builder.Services.AddHostedService<LikesRefreshJob>();
builder.Services.AddHostedService<CleanupOldMatchesHostedService>();

// Livescores DI
builder.Services.AddSingleton<LiveScoresStore>();
builder.Services.AddSingleton<LiveScoresScraperService>();
builder.Services.AddHostedService<LiveScoresRefreshJob>();
builder.Services.AddSingleton<DetailsRefreshService>();

// Trade Signal Webhook DI
builder.Services.AddSingleton<TradeSignalStore>();

// App services
builder.Services.AddSingleton<ResultStore>();
builder.Services.AddSingleton<ScraperService>();
builder.Services.AddHostedService<RefreshJob>();

builder.Services.AddSingleton<DetailsStore>();
builder.Services.AddSingleton<DetailsScraperService>();
builder.Services.AddHostedService<DetailsRefreshJob>();

builder.Services.AddSingleton<TipsStore>();
builder.Services.AddSingleton<TipsScraperService>();
builder.Services.AddHostedService<TipsRefreshJob>();

builder.Services.AddSingleton<Top10Store>();
builder.Services.AddSingleton<Top10ScraperService>();
builder.Services.AddHostedService<Top10RefreshJob>();

builder.Services.AddSingleton<SnapshotPerDateStore>();
builder.Services.AddHostedService<PerDateRefreshJob>();

// NEW: parsed tips editor
builder.Services.AddSingleton<ParsedTipsService>();
//builder.Services.AddHostedService<ParsedTipsRefreshJob>();

builder.Services.AddHostedService<OldDataCleanupJob >();

// ClubElo DI
builder.Services.AddSingleton<ClubEloStore>();
builder.Services.AddSingleton<ClubEloScraperService>();
builder.Services.AddHostedService<ClubEloRefreshJob>();

// Program.cs (server)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = null;       // keep PascalCase
    o.SerializerOptions.DictionaryKeyPolicy = null;        // (optional)
});

var app = builder.Build();

var perDateStore = app.Services.GetRequiredService<SnapshotPerDateStore>();
{
    var center = ScraperConfig.TodayLocal();
    foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
        BulkRefresh.TryLoadFromDisk(perDateStore, d);
}


app.UseResponseCompression();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Build"] = "likes-post-only";
    await next();
});

app.Use(async (ctx, next) =>
{
    await next();
    var ep = ctx.GetEndpoint()?.DisplayName ?? "(no endpoint)";
    Console.WriteLine($"[{ctx.Response.StatusCode}] {ctx.Request.Method} {ctx.Request.Path} -> {ep}");
});

// after: var app = builder.Build();
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host.ToLowerInvariant();
    if (host == "scorespredict.com" || host == "www.scorespredict.com")
    {
        var path = ctx.Request.Path.Value ?? "";

        // Allow reset page + APIs through to the app
        if (path.StartsWith("/reset") || path.StartsWith("/v1/auth/reset")
            || path.StartsWith("/legal") || path.StartsWith("/v1/legal")
            || path.StartsWith("/account/delete")     // allow HTML confirm page
            || path.StartsWith("/v1/auth/account")
			|| path.StartsWith("/app-version.txt")
		    || path.StartsWith("/app-ads.txt"))   // allow the DELETE API
        {
            await next();
            return;
        }

        // Blank home
        if (path == "/" || path == "/index.html")
        {
            ctx.Response.StatusCode = 204;
            return;
        }

        // Everything else is hidden
        ctx.Response.StatusCode = 404;
        return;
    }

    await next();
});

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        // Print to console AND return body so curl shows it
        Console.WriteLine("UNHANDLED: " + ex);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(ex.ToString());
        }
    }
});


// Show errors while debugging
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
    app.MapGet("/error", () => Results.Problem("An error occurred."));
}

// GET /data/clubelo/current?limit=50
app.MapGet("/data/clubelo/current", ([FromServices] ClubEloStore store, [FromQuery] int? limit) =>
{
    var ranks = store.GetCurrentRanks();
    if (ranks is null) return Results.NotFound(new { error = "clubelo current not ready" });

    var take = (limit is > 0) ? limit.Value : ranks.Count;
    return Results.Json(new
    {
        UpdatedUtc = store.LastRanksFetchUtc,
        Count = Math.Min(take, ranks.Count),
        Items = ranks.Take(take)
    });
});

// GET /data/clubelo/fixtures?date=yyyy-MM-dd   (default: today, window today±3)
app.MapGet("/data/clubelo/fixtures", ([FromServices] ClubEloStore store, [FromQuery] string? date) =>
{
    var center = ScraperConfig.TodayLocal(); // you already use this elsewhere
    var min = center.AddDays(-3);
    var max = center.AddDays(+3);

    DateOnly d;
    if (string.IsNullOrWhiteSpace(date))
        d = center;
    else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
        return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });

    if (d < min || d > max)
        return Results.BadRequest(new { error = "date outside allowed window", window = new { from = min.ToString("yyyy-MM-dd"), to = max.ToString("yyyy-MM-dd") } });

    var key = d.ToString("yyyy-MM-dd");
    var items = store.GetFixturesForDate(key);

    if (items is null) return Results.NotFound(new { error = "clubelo fixtures not ready" });

    return Results.Json(new
    {
        UpdatedUtc = store.LastFixturesFetchUtc,
        Date = key,
        Window = new { from = min.ToString("yyyy-MM-dd"), to = max.ToString("yyyy-MM-dd") },
        Count = items.Count,
        Items = items
    });
});

// GET /v1/time/now  — returns server UTC & server-local time
app.MapGet("/v1/time/now", () =>
{
    var nowUtc = DateTimeOffset.UtcNow;

    // Reuse your server's configured/local TZ (you already honor env TOP_OF_HOUR_TZ elsewhere)
    var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
    var tz   = !string.IsNullOrWhiteSpace(tzId)
        ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
        : TimeZoneInfo.Local;

    var local = TimeZoneInfo.ConvertTime(nowUtc, tz);

    // Lowercase keys to match your other minimal endpoints
    return Results.Ok(new
    {
        utc      = nowUtc,                  // ISO-8601 with offset
        local    = local,                  // ISO-8601 with offset in server TZ
        tz       = tz.Id,                  // e.g., "Europe/Brussels"
        epochSec = nowUtc.ToUnixTimeSeconds()
    });
});

app.MapGet("/app-ads.txt", async ctx =>
{
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    // cache a bit (adjust as you like)
    ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
    await ctx.Response.WriteAsync("google.com, pub-3388518972215761, DIRECT, f08c47fec0942fa0");
});

app.MapGet("/app-version.txt", async ctx =>
{
    // Allow override via env var APP_VERSION_FILE, otherwise use default path
    var path = Environment.GetEnvironmentVariable("APP_VERSION_FILE")
               ?? "/var/lib/datasvc/app-version.txt";

    ctx.Response.ContentType = "text/plain; charset=utf-8";
    // avoid stale clients; you can change this to max-age=60 if you like
    ctx.Response.Headers["Cache-Control"] = "no-store";

    if (!System.IO.File.Exists(path))
    {
        // Fallback when file isn't there yet
        await ctx.Response.WriteAsync("AI Scores Predictor, v?.?.?, code ?, date ?");
        return;
    }

    var fi = new FileInfo(path);

    // Support If-Modified-Since (cheap caching)
    if (ctx.Request.Headers.TryGetValue("If-Modified-Since", out var ims) &&
        DateTimeOffset.TryParse(ims.ToString(), out var since) &&
        fi.LastWriteTimeUtc <= since.UtcDateTime.AddSeconds(1))
    {
        ctx.Response.StatusCode = StatusCodes.Status304NotModified;
        return;
    }

    ctx.Response.Headers["Last-Modified"] = fi.LastWriteTimeUtc.ToString("R");

    // Stream file without loading into memory
    await ctx.Response.SendFileAsync(path);
});

app.MapPost("/__likes_probe", (HttpContext ctx) =>
{
    Console.WriteLine("HIT __likes_probe, auth=" + (ctx.User?.Identity?.IsAuthenticated ?? false));
    return Results.Ok(new { ok = true, auth = ctx.User?.Identity?.IsAuthenticated ?? false });
});

// POST /data/likes/recompute?hour=13
// Re-applies user vote totals to all snapshots for the chosen hour (UTC).
app.MapPost("/data/likes/recompute", async (
    [FromServices] ILogger<LikesRefreshJob> log,
    [FromServices] ResultStore root,
    [FromServices] SnapshotPerDateStore perDateStore,
    [FromServices] IConfiguration cfg,
    [FromQuery] int? hour,                 // 0..23 (UTC)
    CancellationToken ct
) =>
{
    var nowUtc = DateTime.UtcNow;

    // validate/select hour
    var hourToUse = hour ?? nowUtc.Hour;
    if (hourToUse < 0 || hourToUse > 23)
        return Results.BadRequest(new { ok = false, error = "hour must be 0..23 (UTC)" });

    // open DB once for the whole recompute pass
    var cs = cfg.GetConnectionString("Default");
    await using var conn = new MySqlConnection(cs);
    await conn.OpenAsync(ct);

    int groupsTouched = 0, itemsTouched = 0;

    try
    {
        // 1) Current snapshot (the main/home group)
        var current = root.Current;
        if (current?.Payload?.TableDataGroup is not null && current.Payload.TableDataGroup.Count > 0)
        {
            var whenUtcCurrent = new DateTime(
                nowUtc.Year, nowUtc.Month, nowUtc.Day,
                hourToUse, 0, 0, DateTimeKind.Utc);

            await VoteMixing.ApplyUserVotesAsync(current.Payload.TableDataGroup, whenUtcCurrent, nowUtc, conn, ct);

            groupsTouched += current.Payload.TableDataGroup.Count;
            itemsTouched  += current.Payload.TableDataGroup.Sum(g => g?.Items?.Count ?? 0);
        }

        // 2) Per-date snapshots across today±3 using the same hour
        var center = ScraperConfig.TodayLocal();
        foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
        {
            if (perDateStore.TryGet(d, out var snap) && snap?.Payload?.TableDataGroup is not null && snap.Payload.TableDataGroup.Count > 0)
            {
                var whenUtcPerDate = new DateTime(d.Year, d.Month, d.Day, hourToUse, 0, 0, DateTimeKind.Utc);

                await VoteMixing.ApplyUserVotesAsync(snap.Payload.TableDataGroup, whenUtcPerDate, nowUtc, conn, ct);

                groupsTouched += snap.Payload.TableDataGroup.Count;
                itemsTouched  += snap.Payload.TableDataGroup.Sum(g => g?.Items?.Count ?? 0);
            }
        }

        log.LogInformation("Manual likes recompute completed: hour={Hour}Z, groups={Groups}, items={Items}",
            hourToUse, groupsTouched, itemsTouched);

        return Results.Ok(new { ok = true, hourUtc = hourToUse, groupsTouched, itemsTouched });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Manual likes recompute failed for hour={Hour}", hourToUse);
        return Results.Problem(title: "likes recompute failed", detail: ex.Message);
    }
});

// GET /data/tips/dates  -> lists available per-date snapshots for Tips
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

app.MapGet("/data/top10/dates", () =>
{
    var center = ScraperConfig.TodayLocal();
    return Results.Ok(new
    {
        window = new {
            today = center.ToString("yyyy-MM-dd"),
            from  = center.AddDays(-3).ToString("yyyy-MM-dd"),
            to    = center.ToString("yyyy-MM-dd")
        },
        dates = Top10PerDateFiles.ListDates()
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

///New bulk endpoints added
// POST /data/refresh-window?date=YYYY-MM-DD&daysBack=3&daysAhead=3
app.MapPost("/data/refresh-window", async (
    [FromQuery] string? date,
    [FromQuery] int? daysBack,
    [FromQuery] int? daysAhead,
	[FromQuery] int? hour,  
    [FromServices] SnapshotPerDateStore perDateStore,
    [FromServices] IConfiguration cfg,
	[FromServices] ParsedTipsService tips,        // <-- add this
    CancellationToken ct) =>
{
    var center = date is null ? ScraperConfig.TodayLocal() : DateOnly.Parse(date);
    var back   = daysBack  ?? 3;
    var ahead  = daysAhead ?? 3;

    var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
        store:  perDateStore,
        cfg:    cfg,
		tips:   tips,           // <-- pass it through
		hourUtc: hour,
        center: center,
        back:   back,
        ahead:  ahead,
        ct:     ct);

    BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

    return Results.Ok(new {
        center = center.ToString("yyyy-MM-dd"),
        back, ahead,
        refreshed,
        errors,
        ok = errors.Count == 0
    });
});


// GET /data/refresh-window?date=YYYY-MM-DD&daysBack=3&daysAhead=3
app.MapGet("/data/refresh-window", async (
    [FromQuery] string? date,
    [FromQuery] int? daysBack,
    [FromQuery] int? daysAhead,
	[FromQuery] int? hour,  
    [FromServices] SnapshotPerDateStore perDateStore,
    [FromServices] IConfiguration cfg,
	[FromServices] ParsedTipsService tips,
    CancellationToken ct) =>
{
    var center = date is null ? ScraperConfig.TodayLocal() : DateOnly.Parse(date);
    var back   = daysBack  ?? 3;
    var ahead  = daysAhead ?? 3;

    var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
        store: perDateStore,
        cfg:   cfg,
		tips:   tips,
		hourUtc: hour,
        center: center,
        back:   back,
        ahead:  ahead,
        ct:     ct);

    BulkRefresh.CleanupRetention(perDateStore, center, back, ahead);

    return Results.Ok(new {
        center = center.ToString("yyyy-MM-dd"),
        back, ahead,
        refreshed,
        errors,
        ok = errors.Count == 0
    });
});

// GET /data/parsed/date/{date}
app.MapGet("/data/parsed/date/{date}", (string date) =>
{
    var d = DateOnly.Parse(date);
    return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
        ? Results.Ok(snap.Payload.TableDataGroup)
        : Results.NotFound(new { error = "snapshot not found; refresh first", date });
});
/*
// GET /data/parsed/date/{date}
app.MapGet("/data/parsed/date/{date}", (
    string date,
    [FromServices] SnapshotPerDateStore perDateStore,
    [FromServices] ParsedTipsService tipsService) =>
{
    var d = DateOnly.Parse(date);

    if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
        return Results.NotFound(new { error = "snapshot not found; refresh first", date });

    var groups = snap.Payload.TableDataGroup;
    tipsService.ApplyTipsForDate(d, groups);

    return Results.Ok(groups);
});

// GET /data/parsed/date/{date}
app.MapGet("/data/parsed/date/{date}", async (
    string date,
    SnapshotPerDateStore perDateStore,
    ParsedTipsService tips,
    CancellationToken ct) =>
{
    var d = DateOnly.Parse(date);

    if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
        return Results.NotFound(new { error = "snapshot not found; refresh first", date });

    var groups = snap.Payload.TableDataGroup;

    await tips.ApplyTipsForDate(d, groups, ct);

    return Results.Ok(groups);
});
*/

// GET /data/html/date/{date}
app.MapGet("/data/html/date/{date}", (string date) =>
{
    var d = DateOnly.Parse(date);
    return perDateStore.TryGet(d, out var snap) && snap.Payload is not null
        ? Results.Text(snap.Payload.HtmlContent ?? "", "text/html")
        : Results.NotFound(new { error = "snapshot not found; refresh first", date });
});

// GET /data/snapshot/date/{date}
app.MapGet("/data/snapshot/date/{date}", (string date) =>
{
    var d = DateOnly.Parse(date);
    if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
        return Results.NotFound(new { error = "snapshot not found; refresh first", date });

    return Results.Ok(new {
        date = d.ToString("yyyy-MM-dd"),
        lastUpdatedUtc = snap.LastUpdatedUtc,
        tableDataGroup = snap.Payload.TableDataGroup,
        titlesAndHrefs = snap.Payload.TitlesAndHrefs
    });
});
// GET /data/refresh-date/{date}  (eg: /data/refresh-date/2025-10-01?hour=14)
app.MapGet("/data/refresh-date/{date}", async (
    string date,
    [FromQuery] int? hour,                                // optional hour 0..23
    [FromServices] SnapshotPerDateStore perDateStore,     // <-- inject store
    [FromServices] IConfiguration cfg,
	[FromServices] ParsedTipsService tips,
    CancellationToken ct) =>
{
    try
    {
        var d = DateOnly.Parse(date);

        // pass the hour through; null means "use current UTC hour"
        var snap = await ScraperService.FetchOneDateAsync(d, cfg, hour, ct);
		
		// Apply tips once, on refresh
        if (snap.Payload?.TableDataGroup is { } groups && groups.Count > 0)
        {
            await tips.ApplyTipsForDate(d, groups, ct);
        }
		
        perDateStore.Set(d, snap);

        return Results.Ok(new {
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


// Back-compat: current-day shortcuts (resolve to Brussels today)
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

app.MapGet("/data/perdate/status", (
    SnapshotPerDateStore perDateStore,
    DetailsStore detailsStore
) =>
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
        stats                   // NEW
    });
});

/*
app.MapGet("/data/perdate/status", () =>
{
    var center = ScraperConfig.TodayLocal();
    var dir = Path.GetDirectoryName(ScraperConfig.SnapshotPath(center))!;
    var files = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList()
        : new List<string>();

    // If your store doesn’t have Keys(), use TryGet in a loop over files; otherwise:
    var memDates = new List<string>();
    foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
        if (perDateStore.TryGet(d, out _)) memDates.Add(d.ToString("yyyy-MM-dd"));

    return Results.Ok(new {
        tz = ScraperConfig.TimeZone,
        center = center.ToString("yyyy-MM-dd"),
        memoryDates = memDates,     // what the app has in RAM
        diskFiles = files           // what exists on disk
    });
});
*/

// ---------- API ----------
// -------- Trade Signal Webhook --------
// Receives alerts like:
// {"symbol":"{{ticker}}","side":"{{strategy.order.action}}","qty":"{{strategy.order.contracts}}",
//  "price":"{{close}}","trigger_time":"{{timenow}}","max_lag":"20","strategy_id":"d2ecb3e5-1c49-4a05-bbcc-f99313098977"}

app.MapPost("/webhooks/trade",
    ([FromBody] TradeSignal payload, [FromServices] TradeSignalStore store) =>
{
    var receivedAt = DateTimeOffset.UtcNow;

    // Try parse trigger_time as ISO-8601 or Unix seconds/milliseconds
    var (triggerTs, lagSeconds) = TradeSignalUtils.TryParseTriggerTime(payload.TriggerTime);

    // Parse optional max_lag
    int? maxLag = TradeSignalUtils.TryParseInt(payload.MaxLag);

    bool accepted = true;
    string? reason = null;

    if (maxLag.HasValue && lagSeconds.HasValue && lagSeconds.Value > maxLag.Value)
    {
        accepted = false;
        reason = $"Lag {lagSeconds.Value:F1}s exceeds max_lag {maxLag.Value}.";
    }

    var record = new TradeSignalReceived(payload, receivedAt, lagSeconds, accepted, reason);
    store.Add(record);

    if (!accepted)
        return Results.BadRequest(new { ok = false, error = reason, lagSeconds, receivedAtUtc = receivedAt });

    return Results.Ok(new { ok = true, lagSeconds, receivedAtUtc = receivedAt });
});

// Quick inspector to see the last N webhook posts (default 50, max 200).
app.MapGet("/webhooks/trade/recent",
    ([FromServices] TradeSignalStore store, [FromQuery] int take = 50) =>
{
    take = Math.Clamp(take, 1, 200);
    return Results.Json(store.Last(take));
});
//app.MapGet("/", () => Results.Redirect("/data/status"));

app.MapGet("/__routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var lines = new List<string>();
    foreach (var s in sources)
    foreach (var e in s.Endpoints.OfType<RouteEndpoint>())
    {
        var methods = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods
                      ?? new[] { "(any)" };
        lines.Add($"{string.Join(",", methods)} {e.RoutePattern.RawText}");
    }
    return Results.Text(string.Join("\n", lines), "text/plain");
});

app.MapGet("/account/delete", async ctx =>
{
    var token = ctx.Request.Query["token"].ToString(); // optional
    var html = @$"<!DOCTYPE html>
					<html>
					<head>
					  <meta charset=""utf-8"">
					  <title>Delete Account</title>
					  <meta name=""referrer"" content=""no-referrer"">
					  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
					</head>
					<body style=""font-family:sans-serif;max-width:520px;margin:4rem auto"">
					  <h1>Delete your account</h1>
					  <p>This permanently removes your account, sessions, identities and profile. This cannot be undone.</p>
					
					  <form id=""form"">
					    <label>Password (leave blank if you signed up with Google only)</label><br>
					    <input id=""pw"" type=""password"" style=""width:100%;padding:8px""><br><br>
					    <button id=""btn"" type=""submit"">Delete my account</button>
					  </form>
					
					  <p id=""msg"" style=""margin-top:1rem;color:#b00""></p>
					
					  <script>
					    const form  = document.getElementById('form');
					    const btn   = document.getElementById('btn');
					    const msg   = document.getElementById('msg');
					    const token = {System.Text.Json.JsonSerializer.Serialize(token)};
					
					    form.addEventListener('submit', async (e) => {{
					      e.preventDefault();
					      btn.disabled = true;
					      msg.textContent = '';
					      const pw = document.getElementById('pw').value;
					
					      const headers = {{ 'Content-Type': 'application/json' }};
					      if (token) headers['Authorization'] = 'Bearer ' + token;
					
					      try {{
					        const r = await fetch('/v1/auth/account', {{
					          method: 'DELETE',
					          headers,
					          body: JSON.stringify({{ password: pw }})
					        }});
					        if (r.status === 204) {{
					          msg.style.color = '#070';
					          msg.textContent = 'Your account has been deleted.';
					          form.style.display = 'none';
					          return;
					        }}
					        const body = await r.text();
					        msg.textContent = 'Delete failed (' + r.status + '). ' + (body || 'Please check your password or token.');
					      }} catch (err) {{
					        msg.textContent = 'Network error. Please try again.';
					      }} finally {{
					        btn.disabled = false;
					      }}
					    }});
					  </script>
					</body>
					</html>";
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"]        = "no-cache";
    ctx.Response.Headers["Expires"]       = "0";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html);
});


app.MapGet("/reset", async ctx =>
{
    var token = ctx.Request.Query["token"].ToString();

    // Only show the page if a 64-hex token is present
    bool hasToken = !string.IsNullOrEmpty(token)
                    && token.Length == 64
                    && System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-fA-F]{64}$");

    if (!hasToken)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var encodedToken = WebUtility.HtmlEncode(token);
	var html = @$"<!DOCTYPE html>
					<html>
					<head>
					  <meta charset=""utf-8"">
					  <title>Reset Password</title>
					  <meta name=""referrer"" content=""no-referrer"">
					  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
					</head>
					<body style=""font-family:sans-serif;max-width:480px;margin:4rem auto"">
					  <h1>Reset password</h1>
					
					  <!-- hidden until token is validated -->
					  <form id=""form"" style=""display:none"">
					    <input type=""hidden"" id=""token"" value=""{encodedToken}"">
					    <label>New password</label><br>
					    <input id=""pw"" type=""password"" minlength=""8"" required style=""width:100%;padding:8px""><br><br>
					    <button id=""btn"" type=""submit"">Reset</button>
					  </form>
					
					  <p id=""msg"">Validating link…</p>
					
					  <script>
					    const form = document.getElementById('form');
					    const btn  = document.getElementById('btn');
					    const msg  = document.getElementById('msg');
					    const token = document.getElementById('token').value;
					
					    // 1) validate first, then show/hide the form
					    (async function validate() {{
					      try {{
					        const r = await fetch('/v1/auth/reset/validate?token=' + encodeURIComponent(token), {{
					          method: 'GET',
					          cache: 'no-store',
					          credentials: 'omit'
					        }});
					        if (r.status === 204) {{
					          form.style.display = 'block';
					          msg.textContent = '';
					        }} else {{
					          msg.textContent = 'Reset link is invalid or expired.';
					        }}
					      }} catch {{
					        msg.textContent = 'Network error while validating.';
					      }}
					    }})();
					
					    // 2) submit handler
					    form.addEventListener('submit', async function doReset(e) {{
					      e.preventDefault();
					      btn.disabled = true;
					      const pw = document.getElementById('pw').value;
					
					      try {{
					        const r = await fetch('/v1/auth/reset', {{
					          method: 'POST',
					          headers: {{ 'Content-Type': 'application/json' }},
					          body: JSON.stringify({{ token: token, newPassword: pw }}),
					          cache: 'no-store',
					          credentials: 'omit'
					        }});
							const bodyText = await r.text(); // read message if any
					        if (r.ok) {{
					          msg.textContent = 'Password changed. You can close this tab.';
					          form.style.display = 'none';
					        }} else {{
					          msg.textContent = 'Reset failed (' + r.status + '). ' + (bodyText || 'The link may be invalid or expired.');
					          btn.disabled = false;
					        }}
					      }} catch {{
					        msg.textContent = 'Network error. Please try again.';
					        btn.disabled = false;
					      }}
					    }});
					  </script>
					</body>
					</html>";
	ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
	ctx.Response.Headers["Pragma"] = "no-cache";
	ctx.Response.Headers["Expires"] = "0";
	ctx.Response.ContentType = "text/html; charset=utf-8";
	await ctx.Response.WriteAsync(html);

});

// ---- Public legal pages (HTML) ----
app.MapGet("/legal/{docKey:regex(^(terms|privacy)$)}", async (
    HttpContext ctx,
    string docKey,
    string? lang,
    IConfiguration cfg,
    IWebHostEnvironment env) =>
{
    string language = string.IsNullOrWhiteSpace(lang) ? "en" : lang;

    var section = cfg.GetSection($"Legal:{docKey}");
    var version = section.GetValue<int>("Version", 1);
    var fileRel = section.GetValue<string>("Path", $"Legal/{docKey}_{language}.html");
    var full = Path.Combine(env.ContentRootPath, fileRel);

    if (!System.IO.File.Exists(full))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { message = $"Legal doc not found: {fileRel}" });
        return;
    }

    var html = System.IO.File.ReadAllText(full);

    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
    ctx.Response.Headers["X-Legal-DocKey"] = docKey;
    ctx.Response.Headers["X-Legal-Version"] = version.ToString();

    await ctx.Response.WriteAsync(html);
});


/*
app.MapGet("/data/status", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null) return Results.Json(new { ready = false, message = "No data yet. Initial refresh pending." });
    return Results.Json(new { ready = s.Ready, s.LastUpdatedUtc, s.Error });
});
*/
app.MapGet("/debug/auth", (HttpContext ctx) =>
{
    var uidClaim = ctx.User?.FindFirst("uid")?.Value;
    var uidItem  = ctx.Items.TryGetValue("user_id", out var v) ? v?.ToString() : null;
    var auth = ctx.User?.Identity?.IsAuthenticated == true;
    return Results.Json(new { authenticated = auth, uidClaim, uidItem });
}).RequireAuthorization();

// health + debug
app.MapGet("/ping", () => Results.Ok("pong"));
app.MapGet("/debug/cs", (IConfiguration cfg) =>
{
    var cs = cfg.GetConnectionString("Default");
    return string.IsNullOrWhiteSpace(cs) ? Results.Problem("Missing ConnectionStrings:Default")
                                         : Results.Ok("cs-present");
});
app.MapGet("/debug/db", async (IConfiguration cfg) =>
{
    var cs = cfg.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs)) return Results.Problem("Missing ConnectionStrings:Default");
    try { await using var c = new MySqlConnector.MySqlConnection(cs); await c.OpenAsync(); return Results.Ok("db-ok"); }
    catch (Exception ex) { return Results.Problem("DB connect failed: " + ex.Message); }
});

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

/*
// /data/parsed -> groups with metadata + items (DTO)
app.MapGet("/data/parsed", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null)
        return Results.NotFound(new { message = "No data yet" });

    var groups = s.Payload.TableDataGroup ?? new ObservableCollection<TableDataGroup>();
    return Results.Json(groups); // no projection needed
});
*/

// /data/tips -> groups with metadata + items (store-backed, same as /data/parsed)
app.MapGet("/data/tips", ([FromServices] TipsStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null)
        return Results.NotFound(new { message = "No data yet" });

    var groups = s.Payload.TableDataGroup ?? new ObservableCollection<TableDataGroup>();
    return Results.Json(groups);
});

// /data/top10 -> groups with metadata + items (store-backed, same as /data/parsed)
app.MapGet("/data/top10", ([FromServices] Top10Store store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null)
        return Results.NotFound(new { message = "No data yet" });

    var groups = s.Payload.TableDataGroup ?? new ObservableCollection<TableDataGroup>();
    return Results.Json(groups);
});
/*
// Raw HTML snapshot of the main page
app.MapGet("/data/html", ([FromServices] ResultStore store) =>
{
    var s = store.Current;
    if (s is null || s.Payload is null) return Results.NotFound(new { message = "No data yet" });
    return Results.Text(s.Payload.HtmlContent ?? "", "text/html; charset=utf-8");
});
*/
// Saved JSON snapshot (full)
//app.MapGet("/data/snapshot", () => Results.File("/var/lib/datasvc/latest.json", "application/json"));

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
app.MapGet("/data/details/download", (HttpContext ctx) =>
{
    var jsonPath = DetailsFiles.File;           // e.g., /var/lib/datasvc/details.json
    var gzPath   = jsonPath + ".gz";
    if (!System.IO.File.Exists(jsonPath))
        return Results.NotFound(new { message = "No details file yet" });

    var acceptsGzip = ctx.Request.Headers.AcceptEncoding.ToString()
                           .Contains("gzip", StringComparison.OrdinalIgnoreCase);

    var pathToSend = (acceptsGzip && System.IO.File.Exists(gzPath)) ? gzPath : jsonPath;
    var fi = new FileInfo(pathToSend);

    ctx.Response.Headers["Vary"] = "Accept-Encoding";
    ctx.Response.Headers["X-File-Length"] = fi.Length.ToString(); // compressed or raw length, whichever we send

    if (pathToSend.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        ctx.Response.Headers["Content-Encoding"] = "gzip";

    return Results.File(pathToSend, "application/json", enableRangeProcessing: true, fileDownloadName: "details.json");
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

// -------- LiveScores API --------
app.MapGet("/data/livescores/status", ([FromServices] LiveScoresStore store) =>
{
    var dates = store.Dates();
    return Results.Json(new { totalDates = dates.Count, dates, lastSavedUtc = store.LastSavedUtc });
});
/*
app.MapGet("/data/livescores/dates", ([FromServices] LiveScoresStore store) =>
{
    return Results.Json(store.Dates());
});
*/
app.MapGet("/data/livescores/dates", ([FromServices] LiveScoresStore store) =>
{
    var center = ScraperConfig.TodayLocal();
    var tz = ScraperConfig.TimeZone;

    // Dates in the store (strings like "yyyy-MM-dd")
    var dates = (store.Dates() ?? Array.Empty<string>())
        .OrderBy(s => s, StringComparer.Ordinal) // ascending; use Descending if you prefer
        .ToList();

    var stats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    foreach (var dateStr in dates)
    {
        var day = store.Get(dateStr); // <- matches your store signature
        int total = 0, live = 0, finished = 0, scheduled = 0, other = 0;

        if (day is not null)
        {
            // Items collection could be named Items / Matches / Games
            var items = GetEnumerable(day, "Items", "Matches", "Games");

            if (items is not null)
            {
                foreach (var item in items)
                {
                    total++;

                    // Status could be Status / State / Phase
                    var status = GetString(item, "Status", "State", "Phase");
                    var st = (status ?? "").Trim().ToLowerInvariant();

                    if (st is "live" or "inplay" or "in_play" or "playing")
                        live++;
                    else if (st is "finished" or "ended" or "ft" or "fulltime" or "full_time")
                        finished++;
                    else if (st is "scheduled" or "upcoming" or "not_started" or "ns" or "pre")
                        scheduled++;
                    else
                        other++;
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

    // ---- local helpers (non-public, simple reflection) ----
    static System.Collections.IEnumerable? GetEnumerable(object obj, params string[] propNames)
    {
        var t = obj.GetType();
        foreach (var name in propNames)
        {
            var p = t.GetProperty(name);
            if (p?.GetValue(obj) is System.Collections.IEnumerable e) return e;
        }
        return null;
    }

    static string? GetString(object obj, params string[] propNames)
    {
        var t = obj.GetType();
        foreach (var name in propNames)
        {
            var p = t.GetProperty(name);
            var v = p?.GetValue(obj)?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }
});


app.MapGet("/data/livescores", ([FromServices] LiveScoresStore store, [FromQuery] string? date) =>
{
    var center = ScraperConfig.TodayLocal();
    var min = center.AddDays(-3);
    var max = center.AddDays(+3);

    DateOnly d;
    if (string.IsNullOrWhiteSpace(date))
    {
        d = center;
    }
    else if (!DateOnly.TryParse(date, out d))
    {
        return Results.BadRequest(new { message = "invalid date", expectedFormat = "yyyy-MM-dd" });
    }

    if (d < min || d > max)
    {
        return Results.BadRequest(new
        {
            message = "date outside allowed window (today±3)",
            date = d.ToString("yyyy-MM-dd"),
            allowed = new
            {
                from = min.ToString("yyyy-MM-dd"),
                to   = max.ToString("yyyy-MM-dd")
            }
        });
    }

    var key = d.ToString("yyyy-MM-dd");
    var day = store.Get(key);
    return day is null
        ? Results.NotFound(new { message = "No livescores for date (refresh first)", date = key })
        : Results.Json(day);
});


app.MapPost("/data/livescores/refresh",
    async ([FromServices] LiveScoresScraperService svc) =>
{
    var (refreshed, lastUpdatedUtc) = await svc.FetchAndStoreAsync();
    return Results.Json(new { ok = true, refreshed, lastUpdatedUtc });
});

app.MapGet("/data/livescores/download", (HttpContext ctx) =>
{
    var jsonPath = LiveScoresFiles.File;
    var gzPath   = jsonPath + ".gz";
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

// Keep only the last N days (default 4). Also supports dryRun and explicit "keep" list for debugging.
app.MapPost("/data/livescores/cleanup",
    async ([FromServices] LiveScoresStore store,
           [FromQuery] int keepDays = 7,
           [FromQuery] bool dryRun = false) =>
{
    var dates = store.Dates().ToList();
    var keep = dates
        .OrderByDescending(d => d)
        .Take(Math.Max(1, keepDays))
        .ToList();

    var wouldRemove = dates.Count - keep.Count;
    if (dryRun) return Results.Json(new { ok = true, dryRun, wouldRemove, wouldKeep = keep.Count, keep });

    var removed = store.ShrinkTo(keep);
    await LiveScoresFiles.SaveAsync(store);
    return Results.Json(new { ok = true, removed, kept = keep.Count, keep });
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
	
    //var (items, generatedUtc) = store.Export();
	var (items, _) = store.Export();
	
	var byHref = items
	    .OrderBy(i => i.Href, StringComparer.OrdinalIgnoreCase) // stable ordering
	    .ToDictionary(
	        i => i.Href,
	        i =>
	        {
	            var parsedTeamsInfo = preferTeamsInfoHtml ? null : TeamsInfoParser.Parse(i.Payload.TeamsInfoHtml);
	
	            var matchDataBetween = preferMatchBetweenHtml
	                ? null
	                : MatchBetweenHelper.GetMatchDataBetween(i.Payload.MatchBetweenHtml ?? string.Empty);
	
	            var recentMatchesSeparate = preferSeparateMatchesHtml
	                ? null
	                : MatchSeparatelyHelper.GetMatchDataSeparately(i.Payload.TeamMatchesSeparateHtml ?? string.Empty);
	
	            var rawBarCharts = preferBetStatsHtml ? null
	                : BarChartsParser.GetBarChartsData(i.Payload.TeamsBetStatisticsHtml ?? string.Empty);
	
	            var barCharts = rawBarCharts?.Select(b => new {
	                title = b.Title,
	                halfContainerId = b.HalfContainerId,
	                items = b.ToList()
	            }).ToList();
	
	            var matchFacts = preferFactsHtml
	                ? null
	                : MatchFactsParser.GetMatchFacts(i.Payload.FactsHtml);
	
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
	                : TeamStandingsHelper.GetTeamStandings(i.Payload.TeamStandingsHtml ?? string.Empty);
	
	            return new
	            {
	                href           = i.Href,
	                lastUpdatedUtc = i.LastUpdatedUtc,
	
	                teamsInfo     = parsedTeamsInfo,
	                teamsInfoHtml = preferTeamsInfoHtml ? i.Payload.TeamsInfoHtml : null,
	
	                matchDataBetween = matchDataBetween,
	                matchBetweenHtml = preferMatchBetweenHtml ? i.Payload.MatchBetweenHtml : null,
	
	                recentMatchesSeparate     = recentMatchesSeparate,
	                recentMatchesSeparateHtml = preferSeparateMatchesHtml ? i.Payload.TeamMatchesSeparateHtml : null,
	
	                barCharts              = barCharts,
	                teamsBetStatisticsHtml = preferBetStatsHtml ? i.Payload.TeamsBetStatisticsHtml : null,
	
	                matchFacts = matchFacts,
	                factsHtml  = preferFactsHtml ? i.Payload.FactsHtml : null,
	
	                lastTeamsWinrate     = lastTeamsWinrate,
	                lastTeamsMatchesHtml = preferLastTeamsHtml ? i.Payload.LastTeamsMatchesHtml : null,
	
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
	    //generatedUtc,
	    items        = byHref
	});

});

// GET /data/details/allhrefs/date/{date}  -> matches parsed per-date behavior
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
    // 1) Resolve the date and fetch the parsed snapshot for that day
    var d = DateOnly.Parse(date);
    if (!perDateStore.TryGet(d, out var snap) || snap.Payload is null)
        return Results.NotFound(new { message = "No parsed snapshot for date (refresh first)", date });

    // 2) Collect hrefs from the per-date TableDataGroup
    var hrefs = snap.Payload.TableDataGroup?
        .SelectMany(g => g.Items)
        .Select(i => i.Href)
        .Where(h => !string.IsNullOrWhiteSpace(h))
        .Select(DetailsStore.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? new List<string>();

	// preserve the parsed/day order
	var index = hrefs
			    .Select((h, i) => (h, i))
			    .ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);
	
	var records = hrefs
			    .Select(h => store.Get(h))
			    .Where(r => r is not null)
			    .Cast<DetailsRecord>()
			    .OrderBy(r => index[r.Href])   // stable order
			    .ToList();

	/*
    // 3) Pull only the requested hrefs from DetailsStore
    var records = hrefs
        .Select(h => store.Get(h))
        .Where(r => r is not null)
        .Cast<DetailsRecord>()
        .OrderByDescending(r => r.LastUpdatedUtc) // stable ordering like the non-date endpoint
        .ToList();
	*/

    // Toggles (same behavior as the existing allhrefs endpoint)
    bool preferTeamsInfoHtml       = string.Equals(teamsInfo, "html", StringComparison.OrdinalIgnoreCase);
    bool preferMatchBetweenHtml    = string.Equals(matchBetween, "html", StringComparison.OrdinalIgnoreCase);
    bool preferSeparateMatchesHtml = string.Equals(separateMatches, "html", StringComparison.OrdinalIgnoreCase);
    bool preferBetStatsHtml        = string.Equals(betStats, "html", StringComparison.OrdinalIgnoreCase);
    bool preferFactsHtml           = string.Equals(facts, "html", StringComparison.OrdinalIgnoreCase);
    bool preferLastTeamsHtml       = string.Equals(lastTeamsMatches, "html", StringComparison.OrdinalIgnoreCase);
    bool preferTeamsStatisticsHtml = string.Equals(teamsStatistics, "html", StringComparison.OrdinalIgnoreCase);
    bool preferTeamStandingsHtml   = string.Equals(teamStandings, "html", StringComparison.OrdinalIgnoreCase);

    // 4) Build the same shape as /data/details/allhrefs
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
        //generatedUtc = DateTimeOffset.UtcNow,
        items = byHref
    };

    // 5) Persist the per-date aggregate to disk (and .gz)
    _ = DetailsPerDateFiles.SaveAsync(d, envelope);

    return Results.Json(envelope);
});


// GET /data/details/item?href=...
app.MapGet("/data/details/item",
(
    [FromServices] DetailsStore store,
    [FromQuery] string href,
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
    if (string.IsNullOrWhiteSpace(href))
        return Results.BadRequest(new { message = "href is required" });

    var rec = store.Get(href);
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

// GET /data/details/item-by-index?index=0
// index is based on the SAME ordering used in /data/details/allhrefs (LastUpdatedUtc desc)
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

// POST /data/details/items   (optional small-batch to reduce round-trips)
// Body: ["href1","href2",...]
app.MapPost("/data/details/items",
async (
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

// Map MVC controllers (AuthController)
app.MapControllers();
app.Run();


/*
// Helper to produce the SAME shape as /data/details/allhrefs items[]
static object MapDetailsRecordToAllhrefsItem(
    DetailsRecord i,
    bool preferTeamsInfoHtml,
    bool preferMatchBetweenHtml,
    bool preferSeparateMatchesHtml,
    bool preferBetStatsHtml,
    bool preferFactsHtml,
    bool preferLastTeamsHtml,
    bool preferTeamsStatisticsHtml,
    bool preferTeamStandingsHtml)
{
    // Mirrors the mapping used in /data/details/allhrefs
    // (keep these helpers consistent with your existing code)
    var parsedTeamsInfo = preferTeamsInfoHtml ? null : TeamsInfoParser.Parse(i.Payload.TeamsInfoHtml);

    var matchDataBetween = preferMatchBetweenHtml
        ? null
        : MatchBetweenHelper.GetMatchDataBetween(i.Payload.MatchBetweenHtml ?? string.Empty);

    var recentMatchesSeparate = preferSeparateMatchesHtml
        ? null
        : MatchSeparatelyHelper.GetMatchDataSeparately(i.Payload.TeamMatchesSeparateHtml ?? string.Empty);

    var rawBarCharts = preferBetStatsHtml
        ? null
        : BarChartsParser.GetBarChartsData(i.Payload.TeamsBetStatisticsHtml ?? string.Empty);

    var barCharts = rawBarCharts?.Select(b => new {
        title = b.Title,
        halfContainerId = b.HalfContainerId,
        items = b.ToList()
    }).ToList();

    var matchFacts = preferFactsHtml
        ? null
        : MatchFactsParser.GetMatchFacts(i.Payload.FactsHtml);

    object? lastTeamsWinrate = null;
    if (!preferLastTeamsHtml)
    {
        var m = LastTeamsMatchesHelper.GetQuickTableWinratePercentagesFromSeperateTeams(i.Payload.LastTeamsMatchesHtml ?? "");
        lastTeamsWinrate = new {
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
        : TeamStandingsHelper.GetTeamStandings(i.Payload.TeamStandingsHtml ?? string.Empty);

    return new {
        href           = i.Href,
        lastUpdatedUtc = i.LastUpdatedUtc,

        // teams info
        teamsInfo     = parsedTeamsInfo,
        teamsInfoHtml = preferTeamsInfoHtml ? i.Payload.TeamsInfoHtml : null,

        // matches between
        matchDataBetween = matchDataBetween,
        matchBetweenHtml = preferMatchBetweenHtml ? i.Payload.MatchBetweenHtml : null,

        // recent matches (separate)
        recentMatchesSeparate     = recentMatchesSeparate,
        recentMatchesSeparateHtml = preferSeparateMatchesHtml ? i.Payload.TeamMatchesSeparateHtml : null,

        // charts & facts
        barCharts              = barCharts,
        teamsBetStatisticsHtml = preferBetStatsHtml ? i.Payload.TeamsBetStatisticsHtml : null,

        matchFacts = matchFacts,
        factsHtml  = preferFactsHtml ? i.Payload.FactsHtml : null,

        // last teams winrate block
        lastTeamsWinrate     = lastTeamsWinrate,
        lastTeamsMatchesHtml = preferLastTeamsHtml ? i.Payload.LastTeamsMatchesHtml : null,

        // team statistics + standings
        teamsStatistics     = teamsStats,
        teamsStatisticsHtml = preferTeamsStatisticsHtml ? i.Payload.TeamsStatisticsHtml : null,

        teamStandings     = teamStandingsParsed,
        teamStandingsHtml = preferTeamStandingsHtml ? i.Payload.TeamStandingsHtml : null
    };
}
*/


static void SaveGzipCopy(string jsonPath)
{
    var gzPath = jsonPath + ".gz";
    using var input = File.OpenRead(jsonPath);
    using var output = File.Create(gzPath);
    using var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: false);
    input.CopyTo(gz);
}
// ---------- Models & storage ----------
public sealed class ResultStore
{
    private readonly object _gate = new();
    private DataSnapshot? _current;
    public DataSnapshot? Current { get { lock (_gate) return _current; } }
    public void Set(DataSnapshot snap) { lock (_gate) _current = snap; }
}

// Stores one snapshot per date
public sealed class SnapshotPerDateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, DataSnapshot> _byDate = new();

    public void Set(DateOnly date, DataSnapshot snap)
    {
        lock (_gate) _byDate[date] = snap;
    }

    public bool TryGet(DateOnly date, out DataSnapshot snap)
    {
        lock (_gate) return _byDate.TryGetValue(date, out snap!);
    }

    public void PruneTo(HashSet<DateOnly> keep)
    {
        lock (_gate)
        {
            var toRemove = _byDate.Keys.Where(d => !keep.Contains(d)).ToList();
            foreach (var d in toRemove) _byDate.Remove(d);
        }
    }
}

public sealed class ParsedTipsService
{
    private readonly LiveScoresStore _live;
	private readonly ClubEloStore _clubEloStore;

    public ParsedTipsService(LiveScoresStore live, ClubEloStore clubEloStore)
    {
        _live = live;
		_clubEloStore = clubEloStore;
    }

	private Dictionary<string, double> _eloByTeam = new(StringComparer.OrdinalIgnoreCase);
	private DateTimeOffset? _eloStampUtc = null;
	
	private void EnsureEloIndex()
	{
	    // If the store hasn't refreshed yet, just keep empty map
	    var stamp = _clubEloStore.LastRanksFetchUtc;
	    if (stamp is null) return;
	
	    // If already built for this stamp, no work
	    if (_eloStampUtc == stamp && _eloByTeam.Count > 0)
	        return;
	
	    var ranks = _clubEloStore.GetCurrentRanks();
	    if (ranks is null || ranks.Count == 0)
	        return;
	
	    var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
	
	    foreach (var r in ranks)
	    {
	        if (string.IsNullOrWhiteSpace(r.Club)) continue;
	
	        // Use the same canonicalizer you already use for live matching
	        var key = FixtureHelper.Canon(r.Club);
	        if (!dict.ContainsKey(key))
	            dict[key] = r.Elo;
	    }
	
	    _eloByTeam = dict;
	    _eloStampUtc = stamp;
	}
	
	private bool TryGetElo(string? teamName, out double elo)
	{
	    elo = default;
	    if (string.IsNullOrWhiteSpace(teamName)) return false;
	
	    var key = FixtureHelper.Canon(teamName);
	    return _eloByTeam.TryGetValue(key, out elo);
	}

    // Envelope saved by /data/details/allhrefs/date/{date}
    private sealed class DetailsPerDateEnvelope
    {
        public string? Date { get; set; }
        public int Total { get; set; }
        public DateTimeOffset GeneratedUtc { get; set; }
        public Dictionary<string, DetailsItemDto>? Items { get; set; }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

	private static LiveScoresResponse ToResponse(LiveScoreDay day)
	{
	    return new LiveScoresResponse
	    {
	        Date = day.Date,
	        Groups = day.Groups?.Select(g => new LiveScoreGroupResponse
	        {
	            Competition = g.Competition,
	            Matches = g.Matches?.Select(m => new LiveScoreItemResponse
	            {
	                Time = m.Time,
	                Status = m.Status,
	                HomeTeam = m.HomeTeam,
	                HomeGoals = m.HomeGoals,
	                AwayGoals = m.AwayGoals,
	                AwayTeam = m.AwayTeam,
	                Action = m.Action,     // same type in both models
	                MatchID = m.MatchID
	            }).ToList() ?? new List<LiveScoreItemResponse>()
	        }).ToList() ?? new List<LiveScoreGroupResponse>()
	    };
	}
	private static DetailsItemDto TrimForVm(DetailsItemDto src)
	{
	    return new DetailsItemDto
	    {
	        TeamsInfo     = src.TeamsInfo,
	        MatchFacts    = src.MatchFacts,
	        TeamStandings = src.TeamStandings,
	        MatchDataBetween = src.MatchDataBetween,
	        BarCharts = src.BarCharts
	    };
	}

    /// <summary>
    /// Apply tips for the given date:
    ///  - read live scores from LiveScoresStore and map to LiveTableDataGroupDto
    ///  - read per-date details JSON and build href → DetailsItemDto lookup
    ///  - for every parsed item: join by href, analyze, set Tip/ProposedResults/IsVipMatch
    /// </summary>
    public async Task ApplyTipsForDate(
	        DateOnly date,
	        ObservableCollection<TableDataGroup>? groups,
	        CancellationToken ct = default)
	{
		var debugTips = string.Equals(
		    Environment.GetEnvironmentVariable("TIPS_DEBUG"),
		    "1",
		    StringComparison.OrdinalIgnoreCase);
		
		//var dateKey = date.ToString("yyyy-MM-dd");

	    if (groups is null || groups.Count == 0) return;
	
	    // ---------- 0) Livescores for the date (from in-memory store) ----------
	    var dateKey = date.ToString("yyyy-MM-dd");
	    var liveResponse = _live.Get(dateKey); // your store returns the API-shaped model
	
	    var allLiveResults = new ObservableCollection<LiveTableDataGroupDto>();
	    try
	    {
	        allLiveResults = liveResponse != null
	            ? DtoMapper.Map(ToResponse(liveResponse))    // <— adapt to DTO shape
	            : new ObservableCollection<LiveTableDataGroupDto>();
	    }
	    catch (Exception ex)
	    {
	        // make livescores optional — never crash tips
	        Console.WriteLine($"[Tips] Live mapping failed for {dateKey}: {ex.Message}");
	        allLiveResults = new ObservableCollection<LiveTableDataGroupDto>();
	    }
	
	    var teamMatches = allLiveResults
	        .Where(g => g is not null)
	        .SelectMany(g => g.Select(i => new { Group = g, Item = i }))
	        .Where(x => x.Item is not null)
	        .ToList();
	
	    var liveIndex = teamMatches.Select(x => new
	    {
	        x.Group,
	        x.Item,
	        HomeKey = FixtureHelper.Canon(x.Item.HomeTeam ?? string.Empty), // helper you added
	        AwayKey = FixtureHelper.Canon(x.Item.AwayTeam ?? string.Empty),
	        HomeSet = ToSet(FixtureHelper.TokenSet(x.Item.HomeTeam ?? string.Empty)),
			AwaySet = ToSet(FixtureHelper.TokenSet(x.Item.AwayTeam ?? string.Empty)),
	        Kick = SafeParseKick(x.Item.Time) // "HH:mm" -> TimeSpan? (nullable)
	    }).ToList();
	
	    // Build a fast lookup: "home|away" => live item
	    var liveByTeams = new Dictionary<string, LiveTableDataItemDto>(StringComparer.OrdinalIgnoreCase);
	
	    foreach (var g in allLiveResults)
	    {
	        if (g is null) continue;
	        foreach (var m in g)
	        {
	            if (m is null) continue;
	            var k = TeamKey(m.HomeTeam, m.AwayTeam);
	            if (!liveByTeams.ContainsKey(k))
	                liveByTeams[k] = m;
	        }
	    }
	
	    // ---------- 1) Load per-date details JSON (already in the DetailsItemDto shape) ----------
	    var detailsByHref = LoadPerDateDetails(date); // href (normalized) → DetailsItemDto
		int total = 0, missingHref = 0, noDetail = 0, schemeMismatch = 0, analyzed = 0, analyzeFailed = 0, emptyProposed = 0;
		var samples = new List<object>(); // keep small
		
		string ForceScheme(string absUri, string scheme)
		{
		    try
		    {
		        var u = new Uri(absUri);
		        var b = new UriBuilder(u) { Scheme = scheme, Port = -1 };
		        return b.Uri.AbsoluteUri;
		    }
		    catch { return absUri; }
		}
	
	    // ---------- 2) Walk parsed items and join ----------
	    foreach (var group in groups)
	    {
	        if (group?.Items is null) continue;
	
	        foreach (var item in group.Items)
	        {
	            if (item is null) continue;

				total++;
	
	            var href = item.Href;
	            if (string.IsNullOrWhiteSpace(href))
				{
				    missingHref++;
				    if (debugTips && samples.Count < 25)
				        samples.Add(new { reason = "missing-href", host = item.HostTeam, guest = item.GuestTeam });
				    continue;
				}
	
	            var normHref = DetailsStore.Normalize(href);
	            if (!detailsByHref.TryGetValue(normHref, out var detailDto) || detailDto is null)
				{
				    noDetail++;
				
				    // detect likely “looks same but differs” cases:
				    var https = ForceScheme(normHref, "https");
				    var http  = ForceScheme(normHref, "http");
				
				    var hasHttps = detailsByHref.ContainsKey(https);
				    var hasHttp  = detailsByHref.ContainsKey(http);
				
				    if ((hasHttps || hasHttp) && !(hasHttps && hasHttp))
				    {
				        schemeMismatch++;
				        if (debugTips && samples.Count < 25)
				            samples.Add(new { reason = "scheme-mismatch", raw = href, norm = normHref, alt = hasHttps ? https : http });
				    }
				    else
				    {
				        if (debugTips && samples.Count < 25)
				            samples.Add(new { reason = "no-details", raw = href, norm = normHref });
				    }
				
				    continue;
				}
	
	            //item.IsVipMatch = true;
				//item.DetailsDto = detailDto;
				item.DetailsItemDto = TrimForVm(detailDto);   // <= embed the small payload
	
	            // Optional: pick up livescore by teams (ready for future rules)
	            LiveTableDataItemDto? live = null;
	            if (!string.IsNullOrWhiteSpace(item.HostTeam) && !string.IsNullOrWhiteSpace(item.GuestTeam))
	            {
	                liveByTeams.TryGetValue(TeamKey(item.HostTeam!, item.GuestTeam!), out live);
	            }
	
	            // ---- Analyzer (defensive) ----
	            List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>? probs = null;
				List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>? probsVIP = null;
	            try
	            {
					analyzed++;
					
	                var hostSafe = item.HostTeam ?? string.Empty;
	                var guestSafe = item.GuestTeam ?? string.Empty;
	
	                EnsureEloIndex();

					double? homeElo = null, awayElo = null;
					if (TryGetElo(hostSafe, out var he)) homeElo = he;
					if (TryGetElo(guestSafe, out var ae)) awayElo = ae;
					
					probs = await Task.Run(
					    () => TipAnalyzer.Analyze(detailDto, hostSafe, guestSafe, item.Tip, null, null),
					    ct
					).ConfigureAwait(false);
					probsVIP = await Task.Run(
					    () => TipAnalyzer.Analyze(detailDto, hostSafe, guestSafe, item.Tip, homeElo, awayElo),
					    ct
					).ConfigureAwait(false);
	            }
	            catch (Exception ex)
	            {
	                Console.WriteLine($"[Tips] Analyze failed for href={normHref}, '{item.HostTeam}' vs '{item.GuestTeam}': {ex.Message}");
	                probs = new List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>();
					probsVIP = new List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>();
					analyzeFailed++;
	            }
	
	            var tipCode = probs?.OrderByDescending(p => p.Probability).FirstOrDefault();
				var tipCodeVIP = probsVIP?.OrderByDescending(p => p.Probability).FirstOrDefault();
	            item.ProposedResults = probs ?? new List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>();
				item.ProposedResultsVIP = probsVIP ?? new List<DataSvc.Analyzer.TipAnalyzer.ProposedResult>();
				if (item.ProposedResults.Count == 0) emptyProposed++;
	            //item.Tip = tipCode?.Code ?? item.Tip;
				item.VIPTip = tipCode?.Code ?? item.Tip;
				item.VIPTipElo = tipCodeVIP?.Code ?? item.Tip;
	
	            var backgroundTipColour = item.BackgroundTipColour;
				var backgroundTipColourVIP = item.BackgroundTipColour;
	
	            // === Smarter fixture lookup ===
	            // Normalize the fixture’s teams and kickoff
	            var homeSet = ToSet(FixtureHelper.TokenSet(item.HostTeam ?? string.Empty));
				var awaySet = ToSet(FixtureHelper.TokenSet(item.GuestTeam ?? string.Empty));
	            var homeKey = string.Join(' ', homeSet);
	            var awayKey = string.Join(' ', awaySet);
	            var fixtureKick = SafeParseKick(item.Time); // nullable
	
	            // Candidate pool: close kickoff (±60 min). If time is missing on either side, keep it permissive.
	            var candidates = liveIndex.Where(c =>
	                fixtureKick is null || c.Kick is null || FixtureHelper.CloseKick(fixtureKick.Value, c.Kick.Value, minutes: 60));
	
	            (double score, dynamic pick)? best = null;
	
	            foreach (var c in candidates)
	            {
	                try
	                {
	                    // Compare both orientations (in case some feeds swap home/away)
	                    var jHome = Math.Max(FixtureHelper.Jaccard(homeSet, c.HomeSet), FixtureHelper.Jaccard(homeSet, c.AwaySet));
	                    var jAway = Math.Max(FixtureHelper.Jaccard(awaySet, c.AwaySet), FixtureHelper.Jaccard(awaySet, c.HomeSet));
	
	                    // small orientation bonus if home-home & away-away match better
	                    var orientBonus =
	                        (FixtureHelper.Jaccard(homeSet, c.HomeSet) + FixtureHelper.Jaccard(awaySet, c.AwaySet)) >
	                        (FixtureHelper.Jaccard(homeSet, c.AwaySet) + FixtureHelper.Jaccard(awaySet, c.HomeSet)) ? 0.05 : 0.0;
	
	                    // lightweight string-level fuzz bonus
	                    var fuzzy = Math.Max(
	                            (FixtureHelper.JaroWinkler(homeKey, c.HomeKey) + FixtureHelper.JaroWinkler(awayKey, c.AwayKey)) / 2.0,
	                            (FixtureHelper.JaroWinkler(homeKey, c.AwayKey) + FixtureHelper.JaroWinkler(awayKey, c.HomeKey)) / 2.0
	                        ) * 0.3;
	
	                    var score = (jHome + jAway) / 2.0 + orientBonus + fuzzy; // 0..1
	                    if (best is null || score > best.Value.score)
	                        best = (score, c);
	                }
	                catch (Exception ex)
	                {
	                    // protect against any unexpected nulls in c.*
	                    Console.WriteLine($"[Tips] Candidate scoring failed: {ex.Message}");
	                }
	            }
	
	            var matched = (best is { score: > 0.55 }) ? best.Value.pick : null;
	
	            // Compute color off-thread, but APPLY on UI
	            string? scoreOne = item.HostScore, scoreTwo = item.GuestScore;
	            if (matched is not null)
	            {
	                try
	                {
	                    scoreOne = matched.Item.HomeGoals?.ToString() ?? scoreOne;
	                    scoreTwo = matched.Item.AwayGoals?.ToString() ?? scoreTwo;
	                    item.HostTeam = matched.Item.HomeTeam ?? item.HostTeam;
	                    item.GuestTeam = matched.Item.AwayTeam ?? item.GuestTeam;
	                }
	                catch { /* keep previous values if anything is off */ }
	            }
	
	            string srcHome = (matched?.Item?.HomeGoals ?? item.HostScore) ?? string.Empty;
	            string srcAway = (matched?.Item?.AwayGoals ?? item.GuestScore) ?? string.Empty;
	
	            // parse them
	            try
	            {
	                if (EvaluationHelper.TryGetScores(srcHome, srcAway, out var home, out var away) && !string.IsNullOrWhiteSpace(item.VIPTip))
	                {
	                    // pass the LIVE dto so IsFinal/clock come from live
	                    backgroundTipColour = EvaluationHelper.EvaluateTipColor(matched?.Item, item, home, away);
	                }
	                else
	                {
	                    backgroundTipColour = AppColors.Black; // still pending / no numbers yet
	                }
					
					if (EvaluationHelper.TryGetScores(srcHome, srcAway, out var homeVIP, out var awayVIP) && !string.IsNullOrWhiteSpace(item.VIPTipElo))
	                {
	                    // pass the LIVE dto so IsFinal/clock come from live
	                    backgroundTipColourVIP = EvaluationHelper.EvaluateTipColor(matched?.Item, item, homeVIP, awayVIP, true);
	                }
	                else
	                {
	                    backgroundTipColourVIP = AppColors.Black; // still pending / no numbers yet
	                }
	            }
	            catch (Exception ex)
	            {
	                Console.WriteLine($"[Tips] EvaluateTipColor failed for href={normHref}: {ex.Message}");
	                backgroundTipColour = AppColors.Black;
					backgroundTipColourVIP = AppColors.Black;
	            }
	
	            //PENDING QEUE
	            // IMPORTANT: property sets on UI thread
	            //item.Tip = tipCode?.Code ?? item.Tip;
				item.VIPTip = tipCode?.Code ?? item.Tip;
				item.VIPTipElo = tipCodeVIP?.Code ?? item.Tip;
	            //item.Tip = "NTM";
	
	            item.HostScore = scoreOne;
	            item.GuestScore = scoreTwo;
	
	            item.BackgroundTipColour = backgroundTipColour;
				item.BackgroundTipColourVIP =backgroundTipColourVIP;
	            //Debug.WriteLine($"Item {item.TeamOne} vs {item.TeamTwo} tip {tipCode.Code} prob {tipCode?.Probability:P1} color {backgroundTipColour}");
	            if (tipCode?.Probability is double p && p > 0.90 || tipCodeVIP?.Probability is double pVIP && pVIP > 0.90)
	            {
	                item.BackgroundColor = AppColors.Goldenrod;
	                item.IsLocked = true;
	                item.TipIsVisible = false;
					item.IsVipMatch = true;
	            }
	            else
	            {
	                item.BackgroundColor = AppColors.White;
	                item.IsLocked = false;
	                item.TipIsVisible = true;
					item.IsVipMatch = false;
	            }

				Console.WriteLine(
			    $"[Tips][{dateKey}] total={total} detailsKeys={detailsByHref.Count} " +
			    $"missingHref={missingHref} noDetail={noDetail} schemeMismatch={schemeMismatch} " +
			    $"analyzed={analyzed} analyzeFailed={analyzeFailed} emptyProposed={emptyProposed}");
			
			if (debugTips && samples.Count > 0)
			{
			    Console.WriteLine($"[Tips][{dateKey}] samples:\n" +
			        JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
			}
	            /*
	            if (item.IsLocked && !string.IsNullOrEmpty(item.Href) &&
	                unlockIndex.TryGetValue(item.Href, out var watchedAtUtc) &&
	                (nowUtc - watchedAtUtc) <= sixHours)
	            {
	                item.IsLocked = false;
	                item.TipIsVisible = true;
	            }
	            if(date.HasValue)
	            {
	                if(date.Value < serverDateConverted)
	                {
	                    // past date—unlock everything
	                    item.IsLocked = false;
	                    item.TipIsVisible = true;
	                }
	            }
	            */
	        }
	    }
	}

	static HashSet<string> ToSet(IEnumerable<string>? src)
	    => src is HashSet<string> hs ? hs
	       : new HashSet<string>(src ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    // ---- local safe helper (keeps your variable names unchanged) ----
    static TimeSpan? SafeParseKick(string? time)
    {
        try { return FixtureHelper.ParseKick(time); }
        catch { return null; }
    }
	
    /// <summary>
    /// Reads /var/lib/datasvc/details/{yyyy-MM-dd}.json, returns href→DetailsItemDto.
    /// </summary>
	/*
    private static Dictionary<string, DetailsItemDto> LoadPerDateDetails(DateOnly date)
    {
        var map = new Dictionary<string, DetailsItemDto>(StringComparer.OrdinalIgnoreCase);

        var path = $"/var/lib/datasvc/details/{date:yyyy-MM-dd}.json";
        if (!File.Exists(path)) return map;

        var json = File.ReadAllText(path);
        var env = JsonSerializer.Deserialize<DetailsPerDateEnvelope>(json, _json);
        if (env?.Items is null || env.Items.Count == 0) return map;

        foreach (var kv in env.Items)
        {
            var norm = DetailsStore.Normalize(kv.Key);
            if (kv.Value is not null && !map.ContainsKey(norm))
                map[norm] = kv.Value;
        }

        return map;
    }
	*/
	private static Dictionary<string, DetailsItemDto> LoadPerDateDetails(DateOnly date)
	{
	    var map = new Dictionary<string, DetailsItemDto>(StringComparer.OrdinalIgnoreCase);
	
	    var path = $"/var/lib/datasvc/details/{date:yyyy-MM-dd}.json";
	    if (!File.Exists(path))
	    {
	        Console.WriteLine($"[Tips][{date:yyyy-MM-dd}] Details file MISSING: {path}");
	        return map;
	    }
	
	    var fi = new FileInfo(path);
	    Console.WriteLine($"[Tips][{date:yyyy-MM-dd}] Details file OK: {path} size={fi.Length}B mtimeUtc={fi.LastWriteTimeUtc:o}");
	
	    var json = File.ReadAllText(path);
	    var env = JsonSerializer.Deserialize<DetailsPerDateEnvelope>(json, _json);
	
	    Console.WriteLine($"[Tips][{date:yyyy-MM-dd}] Details envelope: envTotal={env?.Total} generatedUtc={env?.GeneratedUtc:o} items={(env?.Items?.Count ?? 0)}");
	
	    if (env?.Items is null || env.Items.Count == 0) return map;
	
	    foreach (var kv in env.Items)
	    {
	        var norm = DetailsStore.Normalize(kv.Key);
	        if (kv.Value is not null && !map.ContainsKey(norm))
	            map[norm] = kv.Value;
	    }
	
	    return map;
	}

    private static string TeamKey(string home, string away)
        => NormalizeTeam(home) + "|" + NormalizeTeam(away);

    private static string NormalizeTeam(string s)
        => (s ?? string.Empty).Trim().ToLowerInvariant();
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

// ---------- Tips storage / service / job ----------
// ---------- Tips per-date files ----------
public static class TipsPerDateFiles
{
    public const string Dir = "/var/lib/datasvc/tips";
    public static string PathFor(DateOnly d) => System.IO.Path.Combine(Dir, $"{d:yyyy-MM-dd}.json");

    public static async Task SaveAsync(DateOnly d, DataSnapshot snap, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(PathFor(d), json, ct);
    }

    public static DataSnapshot? Load(DateOnly d)
    {
        var p = PathFor(d);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<DataSnapshot>(File.ReadAllText(p)); }
        catch { return null; }
    }
	// NEW: list available date keys (yyyy-MM-dd), sorted
    public static IReadOnlyList<string> ListDates()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();
        var keys = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (DateOnly.TryParse(name, out _)) keys.Add(name);
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

// ---------- Top10 per-date files ----------
public static class Top10PerDateFiles
{
    public const string Dir = "/var/lib/datasvc/top10";
    public static string PathFor(DateOnly d) => System.IO.Path.Combine(Dir, $"{d:yyyy-MM-dd}.json");

    public static async Task SaveAsync(DateOnly d, DataSnapshot snap, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(PathFor(d), json, ct);
    }

    public static DataSnapshot? Load(DateOnly d)
    {
        var p = PathFor(d);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<DataSnapshot>(File.ReadAllText(p)); }
        catch { return null; }
    }
	
	// NEW
    public static IReadOnlyList<string> ListDates()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();
        var keys = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (DateOnly.TryParse(name, out _)) keys.Add(name);
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

public static class TipsFiles
{
    public const string Dir  = "/var/lib/datasvc";
    public const string File = "/var/lib/datasvc/tips.json";

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

public sealed class TipsStore
{
    private readonly object _gate = new();
    private DataSnapshot? _current;
    public DataSnapshot? Current { get { lock (_gate) return _current; } }
    public void Set(DataSnapshot snap) { lock (_gate) _current = snap; }
}

public sealed class TipsScraperService
{
    private readonly TipsStore _store;
    public TipsScraperService([FromServices] TipsStore store) => _store = store;

    public async Task<DataSnapshot> FetchAndStoreAsync(CancellationToken ct = default)
	{
	    try
	    {
	        var center = ScraperConfig.TodayLocal();
	        var dates = ScraperConfig.DateWindow(center, 3, 0);      // back:3, ahead:0
	
	        DataSnapshot? lastToday = null;
	
	        foreach (var d in dates)
	        {
	            ct.ThrowIfCancellationRequested();
	
	            var iso = d.ToString("yyyy-MM-dd");
	            var url = $"https://www.statarea.com/tips/date/{iso}/";
				//var url = $"http://www.statarea.com/tips/date/{iso}/";
	
	            var html   = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
	            var titles = GetStartupMainTitlesAndHrefs2024.GetStartupMainTitlesAndHrefs(html);
	            var table  = GetStartupMainTableDataGroup2024.GetStartupMainTableDataGroup(html);
	
	            var payload = new DataPayload(html, titles, table);
	            var snap = new DataSnapshot(DateTimeOffset.UtcNow, true, payload, null);
	
	            await TipsPerDateFiles.SaveAsync(d, snap, ct);
	
	            if (d == center)
	                lastToday = snap;
	        }

			// PRUNE: keep only day-3..today files in /var/lib/datasvc/top10
            var keep = dates.Select(d => d.ToString("yyyy-MM-dd"))
                            .ToHashSet(StringComparer.Ordinal);
            PruneOldFiles(TipsPerDateFiles.Dir, keep);              // PRUNE
			
	        if (lastToday is not null)
	        {
	            _store.Set(lastToday);
	            await TipsFiles.SaveAsync(lastToday);                 // keep current-day behavior
	        }
	
	        return lastToday ?? new DataSnapshot(DateTimeOffset.UtcNow, false, null, "No today snapshot");
	    }
	    catch (Exception ex)
	    {
	        var last = _store.Current;
	        var snap = new DataSnapshot(DateTimeOffset.UtcNow, last?.Ready ?? false, last?.Payload, ex.Message);
	        _store.Set(snap);
	        await TipsFiles.SaveAsync(snap);
	        return snap;
	    }
	}
	static void PruneOldFiles(string dir, HashSet<string> keep)
	{
	    if (!Directory.Exists(dir)) return;
	    foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
	    {
	        var name = System.IO.Path.GetFileNameWithoutExtension(f);
	        if (!keep.Contains(name)) { try { File.Delete(f); } catch { } }
	    }
	}
}

public sealed class TipsRefreshJob : BackgroundService
{
    private readonly TipsScraperService _svc;
    private readonly TipsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeZoneInfo _tz;

    public TipsRefreshJob(TipsScraperService svc, TipsStore store)
    {
        _svc = svc;
        _store = store;
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm from disk
        var prev = await TipsFiles.LoadAsync();
        if (prev is not null) _store.Set(prev);

        // Initial run
        await RunSafelyOnce(stoppingToken);

        // Also tick at the top of each hour
        _ = HourlyLoop(stoppingToken);

        // Keep the 5-minute cadence
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyOnce(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task HourlyLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
                int nextHour = nowLocal.Minute == 0 && nowLocal.Second == 0 ? nowLocal.Hour : nowLocal.Hour + 1;
                if (nextHour == 24) nextHour = 0;
                var nextTopLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nextHour, 0, 0, nowLocal.Offset);
                if (nextTopLocal <= nowLocal) nextTopLocal = nextTopLocal.AddHours(1);

                var delay = nextTopLocal - nowLocal;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await RunSafelyOnce(ct);

                using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                while (await hourly.WaitForNextTickAsync(ct))
                    await RunSafelyOnce(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await _svc.FetchAndStoreAsync(ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tips] run failed: {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }
}

// ---------- Top10 storage / service / job ----------
public static class Top10Files
{
    public const string Dir  = "/var/lib/datasvc";
    public const string File = "/var/lib/datasvc/Top10.json";

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

public sealed class Top10Store
{
    private readonly object _gate = new();
    private DataSnapshot? _current;
    public DataSnapshot? Current { get { lock (_gate) return _current; } }
    public void Set(DataSnapshot snap) { lock (_gate) _current = snap; }
}

public sealed class Top10ScraperService
{
    private readonly Top10Store _store;
    public Top10ScraperService([FromServices] Top10Store store) => _store = store;

    public async Task<DataSnapshot> FetchAndStoreAsync(CancellationToken ct = default)
	{
	    try
	    {
	        var center = ScraperConfig.TodayLocal();                 // your existing helper
	        var dates = ScraperConfig.DateWindow(center, 3, 0);      // back:3, ahead:0
	
	        DataSnapshot? lastToday = null;
	
	        foreach (var d in dates)
	        {
	            ct.ThrowIfCancellationRequested();
	
	            var iso = d.ToString("yyyy-MM-dd");
	            var url = $"https://www.statarea.com/toppredictions/date/{iso}/";
				//var url = $"http://www.statarea.com/toppredictions/date/{iso}/";
	
	            // scrape that specific date
	            var html   = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
	            var titles = GetStartupMainTitlesAndHrefs2024.GetStartupMainTitlesAndHrefs(html);
	            var table  = GetStartupMainTableDataGroup2024.GetStartupMainTableDataGroup(html, null, 0);
	
	            var payload = new DataPayload(html, titles, table);
	            var snap = new DataSnapshot(DateTimeOffset.UtcNow, true, payload, null);
	
	            // save per-date file
	            await Top10PerDateFiles.SaveAsync(d, snap, ct);
	
	            if (d == center)
	                lastToday = snap; // keep "current day" as the in-memory Top10
	        }
			
			// PRUNE: keep only day-3..today files in /var/lib/datasvc/top10
            var keep = dates.Select(d => d.ToString("yyyy-MM-dd"))
                            .ToHashSet(StringComparer.Ordinal);
            PruneOldFiles(Top10PerDateFiles.Dir, keep);              // PRUNE
			
	        // keep legacy current-day store behavior
	        if (lastToday is not null)
	        {
	            _store.Set(lastToday);
	            await Top10Files.SaveAsync(lastToday);                // your existing single-file store
	        }
	
	        return lastToday ?? new DataSnapshot(DateTimeOffset.UtcNow, false, null, "No today snapshot");
	    }
	    catch (Exception ex)
	    {
	        var last = _store.Current;
	        var snap = new DataSnapshot(DateTimeOffset.UtcNow, last?.Ready ?? false, last?.Payload, ex.Message);
	        _store.Set(snap);
	        await Top10Files.SaveAsync(snap);
	        return snap;
	    }
	}
	static void PruneOldFiles(string dir, HashSet<string> keep)
	{
	    if (!Directory.Exists(dir)) return;
	    foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
	    {
	        var name = System.IO.Path.GetFileNameWithoutExtension(f);
	        if (!keep.Contains(name)) { try { File.Delete(f); } catch { } }
	    }
	}
}

public sealed class Top10RefreshJob : BackgroundService
{
    private readonly Top10ScraperService _svc;
    private readonly Top10Store _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeZoneInfo _tz;

    public Top10RefreshJob(Top10ScraperService svc, Top10Store store)
    {
        _svc = svc;
        _store = store;
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm from disk
        var prev = await Top10Files.LoadAsync();
        if (prev is not null) _store.Set(prev);

        // Initial run
        await RunSafelyOnce(stoppingToken);

        // Also tick at the top of each hour
        _ = HourlyLoop(stoppingToken);

        // Keep the 5-minute cadence
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyOnce(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task HourlyLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
                int nextHour = nowLocal.Minute == 0 && nowLocal.Second == 0 ? nowLocal.Hour : nowLocal.Hour + 1;
                if (nextHour == 24) nextHour = 0;
                var nextTopLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nextHour, 0, 0, nowLocal.Offset);
                if (nextTopLocal <= nowLocal) nextTopLocal = nextTopLocal.AddHours(1);

                var delay = nextTopLocal - nowLocal;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await RunSafelyOnce(ct);

                using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                while (await hourly.WaitForNextTickAsync(ct))
                    await RunSafelyOnce(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await _svc.FetchAndStoreAsync(ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Top10] run failed: {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }
}

// ---------- LiveScores: scraper service ----------
public sealed class LiveScoresScraperService
{
    private readonly LiveScoresStore _store;
    private readonly TimeZoneInfo _tz;

    public LiveScoresScraperService([FromServices] LiveScoresStore store)
    {
        _store = store;
        // follow same TZ strategy as your Tips job (env TOP_OF_HOUR_TZ or local) :contentReference[oaicite:6]{index=6}
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    static string BuildUrl(DateTime localDay)
    {
        // Statarea typically supports date query; we try param first, fallback to plain page.
        var iso = localDay.ToString("yyyy-MM-dd");
        return $"https://www.statarea.com/livescore/date/{iso}/";
		//return $"http://www.statarea.com/livescore/date/{iso}/";
    }

    public async Task<(int Refreshed, DateTimeOffset LastUpdatedUtc)> FetchAndStoreAsync(CancellationToken ct = default)
	{
	    int refreshed = 0;
	
	    var center = ScraperConfig.TodayLocal();
	    var dates = ScraperConfig.DateWindow(center, back: 3, ahead: 3).ToList();
	
	    foreach (var dateOnly in dates)
	    {
	        ct.ThrowIfCancellationRequested();
	
	        var localDay = new DateTime(dateOnly.Year, dateOnly.Month, dateOnly.Day);
	        var url = BuildUrl(localDay);
	
	        string html;
	        try
	        {
	            html = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
	        }
	        catch
	        {
	            // Fallback for "today" without query, same as before
	            if (dateOnly == center)
	                html = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo("https://www.statarea.com/livescore");
	            else
	                throw;
	        }
	
	        var dateIso = dateOnly.ToString("yyyy-MM-dd");
	        var day = await LiveScoresParser.ParseDay(html, dateIso);
	        _store.Set(day);
	        refreshed++;
	    }
	
	    // Enforce rolling window (keep exactly today±3)
	    var keep = dates.Select(d => d.ToString("yyyy-MM-dd")).ToHashSet(StringComparer.OrdinalIgnoreCase);
	    _store.ShrinkTo(keep);
	
	    await LiveScoresFiles.SaveAsync(_store);
	    return (refreshed, DateTimeOffset.UtcNow);
	}
}

// ---------- LiveScores: background job ----------
public sealed class LiveScoresRefreshJob : BackgroundService
{
    private readonly LiveScoresScraperService _svc;
    private readonly LiveScoresStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LiveScoresRefreshJob(LiveScoresScraperService svc, LiveScoresStore store)
    {
        _svc = svc; _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm from disk
        var prev = await LiveScoresFiles.LoadAsync();
        if (prev is not null) _store.Import(prev.Value.days);

        // Initial run
        await RunSafelyOnce(stoppingToken);

        // Keep the 5-minute cadence (same pattern you use elsewhere) :contentReference[oaicite:8]{index=8}
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyOnce(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try { await _svc.FetchAndStoreAsync(ct); }
        catch { /* swallow; file state remains last good */ }
        finally { _gate.Release(); }
    }
}

public sealed class PerDateRefreshJob : IHostedService, IDisposable
{
    private readonly SnapshotPerDateStore _store;
    private readonly ILogger<PerDateRefreshJob> _log;
    private readonly IConfiguration _cfg;          // <-- inject cfg
	private readonly ParsedTipsService _tips;    // <-- add
	
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PerDateRefreshJob(
        SnapshotPerDateStore store,
        ILogger<PerDateRefreshJob> log,
        IConfiguration cfg,
		ParsedTipsService tips)                       // <-- DI will supply this
    {
        _store = store;
        _log = log;
        _cfg = cfg;
		_tips = tips;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Initial run shortly after startup; then every 5 minutes
        _timer = new Timer(async _ => await TickAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    private async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var center = ScraperConfig.TodayLocal();
			var hourUtc = DateTime.UtcNow.Hour;

            // *** pass cfg as 2nd arg, keep parameter order (or use named args) ***
            var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
			    store: _store,
			    cfg:   _cfg,
				tips:   _tips, 
			    hourUtc: hourUtc,     // << use the current hour
			    center: center,
			    back:   3,
			    ahead:  3);

            if (errors.Count > 0)
                _log.LogWarning("PerDate refresh had {Count} errors: {Errors}", errors.Count, string.Join("; ", errors.Select(kv => $"{kv.Key}:{kv.Value}")));

            // Enforce retention after refresh
            BulkRefresh.CleanupRetention(_store, center, 3, 3);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PerDate refresh tick failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}

public static class BulkRefresh
{
    public static async Task<(IReadOnlyList<string> Refreshed, IReadOnlyDictionary<string,string> Errors)>
    	RefreshWindowAsync(
        SnapshotPerDateStore store,
        IConfiguration cfg,
		ParsedTipsService tips,          // <-- new
        int? hourUtc = null,
        DateOnly? center = null, int back = 3, int ahead = 3,
        CancellationToken ct = default)
    {
        var c = center ?? ScraperConfig.TodayLocal();
        var dates = ScraperConfig.DateWindow(c, back, ahead).ToArray();

        var refreshed = new List<string>(dates.Length);
        var errors = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in dates)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // Use the FetchOneDateAsync overload that takes IConfiguration
                var snap = await ScraperService.FetchOneDateAsync(d, cfg, hourUtc, ct);
				
				// Enrich: apply tips ONCE here, before putting it into the store
		        if (snap.Payload?.TableDataGroup is { } groups && groups.Count > 0)
		        {
		            await tips.ApplyTipsForDate(d, groups, ct);
		        }
				
                store.Set(d, snap);
                refreshed.Add(d.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                errors[d.ToString("yyyy-MM-dd")] = ex.Message;
            }
        }

        return (refreshed, errors);
    }
	
	public static void CleanupRetention(SnapshotPerDateStore store, DateOnly center, int back, int ahead)
    {
        var keep = new HashSet<DateOnly>(ScraperConfig.DateWindow(center, back, ahead));
        // In-memory prune
        store.PruneTo(keep);

        // On-disk prune
        var dir = Path.GetDirectoryName(ScraperConfig.SnapshotPath(center))!;
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // yyyy-MM-dd
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var d) && !keep.Contains(d))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
    }
    public static bool TryLoadFromDisk(SnapshotPerDateStore store, DateOnly date)
    {
        var path = ScraperConfig.SnapshotPath(date);
        if (!File.Exists(path)) return false;
        var json = File.ReadAllText(path);
        var snap = System.Text.Json.JsonSerializer.Deserialize<DataSnapshot>(json);
        if (snap is null) return false;
        store.Set(date, snap);
        return true;
    }
}


// ---------- Background job ----------
public sealed class ScraperService
{
    private readonly ResultStore _store;
    public ScraperService( [FromServices] ResultStore store ) => _store = store;
	private static readonly HttpClient _http = new HttpClient();

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

	public static async Task<DataSnapshot> FetchOneDateAsync(
	    DateOnly date,
	    IConfiguration cfg,
	    int? hourUtc = null,
	    CancellationToken ct = default)
	{
	    var url  = ScraperConfig.UrlFor(date);
	    var html = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
	
	    var titles = GetStartupMainTitlesAndHrefs2024.GetStartupMainTitlesAndHrefs(html);
	
	    // Use provided hour if valid; otherwise current UTC hour
	    var h = (hourUtc is >= 0 and <= 23) ? hourUtc.Value : DateTime.UtcNow.Hour;
	
	    var whenUtc = new DateTime(date.Year, date.Month, date.Day, h, 0, 0, DateTimeKind.Utc);
	    var table   = GetStartupMainTableDataGroup2024.GetStartupMainTableDataGroup(html, whenUtc);
	
	    var nowUtc = DateTime.UtcNow;
	    var cs = cfg.GetConnectionString("Default");
	    await using (var conn = new MySqlConnection(cs))
	    {
	        await conn.OpenAsync(ct);
	        await VoteMixing.ApplyUserVotesAsync(table, whenUtc, nowUtc, conn, ct);
			// NEW: fill comment counts now that we have `table` and an open DB connection
			await CommentCountFiller.FillTopLevelCommentCountsAsync(table, conn, ct);
	    }
	
	    var payload = new DataPayload(html, titles, table);
	    var snap = new DataSnapshot(DateTimeOffset.UtcNow, true, payload, null);
	
	    // Persist to disk (unchanged)
	    var path = ScraperConfig.SnapshotPath(date);
	    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
	    await File.WriteAllTextAsync(path,
	        JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false }), ct);
	
	    return snap;
	}
}

// ---------- ParsedTips: background job ----------
public sealed class ParsedTipsRefreshJob : BackgroundService
{
    private readonly SnapshotPerDateStore _perDate;
    private readonly ParsedTipsService _tips;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ParsedTipsRefreshJob(
        SnapshotPerDateStore perDateStore,
        ParsedTipsService tips)
    {
        _perDate = perDateStore;
        _tips = tips;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // same 5-min cadence pattern as your other jobs
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            // initial run
            await RunOnceSafe(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceSafe(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOnceSafe(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var center = ScraperConfig.TodayLocal(); // you already use this for windows
            foreach (var d in ScraperConfig.DateWindow(center, back: 3, ahead: 3))
            {
                if (!_perDate.TryGet(d, out var snap) || snap?.Payload?.TableDataGroup is null || snap.Payload.TableDataGroup.Count == 0)
                    continue;

                await _tips.ApplyTipsForDate(d, snap.Payload.TableDataGroup, ct); // exact signature in file
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class RefreshJob : BackgroundService
{
    private readonly ScraperService _svc;
    private readonly ResultStore _store;
	private readonly IConfiguration _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeZoneInfo _tz;

    public RefreshJob(ScraperService svc, ResultStore store, IConfiguration cfg) // <-- inject cfg
    {
        _svc = svc; 
        _store = store;
		_cfg = cfg;
        // Use local server timezone by default; allow override via env var TOP_OF_HOUR_TZ (e.g., "Europe/Brussels")
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prev = await DataFiles.LoadAsync();
        if (prev is not null) _store.Set(prev);

        // Initial run
        await RunSafelyOnce(stoppingToken);

        // Kick off the hour-aligned loop in parallel
        _ = HourlyLoop(stoppingToken);

        // Keep the existing 5-minute cadence
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyOnce(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HourlyLoop(CancellationToken ct)
    {
        try
        {
            // Wait until the next top of the hour in the configured timezone
            while (!ct.IsCancellationRequested)
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
                int nextHour = nowLocal.Minute == 0 && nowLocal.Second == 0 ? nowLocal.Hour : nowLocal.Hour + 1;
                if (nextHour == 24) nextHour = 0;
                var nextTopLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nextHour, 0, 0, nowLocal.Offset);
                if (nextTopLocal <= nowLocal) nextTopLocal = nextTopLocal.AddHours(1);

                var delay = nextTopLocal - nowLocal;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await RunSafelyOnce(ct);

                // After the first aligned tick, continue hourly
                using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                while (await hourly.WaitForNextTickAsync(ct))
                {
                    await RunSafelyOnce(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        // If another run is in progress (e.g., 5-min tick collides with hourly), skip this one
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await _svc.FetchAndStoreAsync(ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[refresh] run failed: {ex}");
        }
        finally
        {
            _gate.Release();
        }
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
	            ?? "https://www.statarea.com/predictions";
	
	    var allowHttp = Environment.GetEnvironmentVariable("ALLOW_HTTP_STATAREA") == "1";
	    var target = new Uri(url);
	
	    // 1) Try HTTPS as-is
	    try
	    {
	        using var req = new HttpRequestMessage(HttpMethod.Get, target);
	        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
	        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
	        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
	        req.Headers.Referrer = new Uri("https://www.statarea.com/");
	
	        using var res = await http.SendAsync(req);
	        res.EnsureSuccessStatusCode();
	        return await res.Content.ReadAsStringAsync();
	    }
	    catch (HttpRequestException ex)
	    {
	        // 2) Fallback ONLY for Statarea, ONLY if enabled
	        var host = target.Host.ToLowerInvariant();
	        if (host.EndsWith("statarea.com"))
	        {
	            var httpUri = new UriBuilder(target) { Scheme = "http", Port = -1 }.Uri;
	            Console.WriteLine($"[scraper] TLS error for {target}; falling back to {httpUri}");
	            using var req2 = new HttpRequestMessage(HttpMethod.Get, httpUri);
	            req2.Headers.Referrer = new Uri("http://www.statarea.com/");
	            using var res2 = await http.SendAsync(req2);
	            res2.EnsureSuccessStatusCode();
	            return await res2.Content.ReadAsStringAsync();
	        }
	        throw; // different host -> don't bypass
	    }
	}

	/*
    public static async Task<string> GetStartupMainPageFullInfo(string? url = null)
    {
        url ??= Environment.GetEnvironmentVariable("DATA_SOURCE_URL")
                ?? "https://www.statarea.com/predictions";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,//*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        req.Headers.Referrer = new Uri("https://www.statarea.com/");
		//req.Headers.Referrer = new Uri("http://www.statarea.com/");

        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }
	*/
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
public static class CommentCountFiller
{
    // Fills top-level (non-reply) comment counts per match/href into your table items.
    // Uses your "matches" table via href_hash → match_id, then counts only visible top-level comments.
    public static async Task FillTopLevelCommentCountsAsync(
        ObservableCollection<TableDataGroup> groups,
        MySqlConnection conn,
        CancellationToken ct = default)
    {
        // 1) collect distinct hrefs from all items
        var hrefs = groups
            .SelectMany(g => g.Items ?? Enumerable.Empty<TableDataItem>())
            .Select(i => i.Href)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (hrefs.Count == 0) return;

        // 2) map href -> match_id (try the 3 variants like in CommentsController)
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var href in hrefs)
        {
            var (h1, h2, h3) = CanonicalHrefCandidates(href);
            var h1Hash = Sha256(h1);
            var h2Hash = h2 is null ? null : Sha256(h2);
            var h3Hash = h3 is null ? null : Sha256(h3);

            var mid = await conn.ExecuteScalarAsync<ulong?>(@"
                SELECT match_id FROM matches
                 WHERE href_hash = @h1Hash
                    OR (@h2Hash IS NOT NULL AND href_hash = @h2Hash)
                    OR (@h3Hash IS NOT NULL AND href_hash = @h3Hash)
                 LIMIT 1;", new { h1Hash, h2Hash, h3Hash });

            if (mid.HasValue) map[href] = mid.Value;
        }

        if (map.Count == 0) return;

        // 3) fetch counts per match_id; visible-only and top-level only (parent_comment_id IS NULL)
        foreach (var kv in map)
        {
            var topLevel = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM comments
                 WHERE match_id=@mid
                   AND (is_deleted = 0 OR is_deleted IS NULL)
                   AND parent_comment_id IS NULL;", new { mid = kv.Value });

            // 4) apply to all items with this href
            foreach (var item in groups.SelectMany(g => g.Items).Where(i => string.Equals(i.Href, kv.Key, StringComparison.OrdinalIgnoreCase)))
                item.Comments = topLevel; // your TableDataItem is mutated elsewhere too, so this matches your pattern
        }

        // --- local helpers copied to keep this file self-contained ---
        static (string primary, string? altPlus, string? altPct20) CanonicalHrefCandidates(string href)
        {
            var primary = href.Trim();
            string? altPlus = null, altPct20 = null;
            if (primary.IndexOf(' ') >= 0)
            {
                altPlus  = primary.Replace(' ', '+');
                altPct20 = primary.Replace(" ", "%20");
            }
            return (primary, altPlus, altPct20);
        }

        static byte[] Sha256(string s) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty));
    }
}

public static class GetStartupMainTableDataGroup2024
{
    public static ObservableCollection<TableDataGroup> GetStartupMainTableDataGroup(string htmlContent, DateTime? date = null, int contrainerSkip = 1) // 0 for Top10
    {
        try
        {
			// ---- pick the effective UTC moment for the scrape ----
	        DateTime whenUtc = date ?? DateTime.UtcNow;                 // fallback to now if null
	        if (whenUtc.Kind != DateTimeKind.Utc) whenUtc = whenUtc.ToUniversalTime();
			
            var website = new HtmlDocument();
            website.LoadHtml(htmlContent);

            var tableDataGroup = new ObservableCollection<TableDataGroup>();

            var matchesGroups = website.DocumentNode.Descendants("div")
							    .FirstOrDefault(o => o.GetAttributeValue("class", "") == "datacotainer full")?
							    .Descendants("div")
							    .Where(o => o.GetAttributeValue("class", "") == "predictions")
							    .Skip(contrainerSkip) // skip the toolbar block; include all subsequent prediction sections
							    .SelectMany(pred => pred.Elements("div")
								.Where(o => o.Attributes["id"] != null)) // collect all competition blocks from all sections
							    .ToList();


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

                            var backgroundtipcolor = AppColors.Black;
                            if (tip != null)
                            {
                                var tipClass = tip.Attributes["class"].Value;
                                if (tipClass == "value success") backgroundtipcolor = AppColors.Green;
                                else if (tipClass == "value failed") backgroundtipcolor = AppColors.Red;
                                else backgroundtipcolor = AppColors.Black;
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
							var likesRaw = likepositive ?? "1";
							// after you've extracted host/guest team display names:
							var hostName = teamone ?? "A";
							var guestName = teamtwo ?? "B";

							// compute server-side using the chosen UTC moment
	                        var computed    = LikesCalculator.ComputeWithDateRules(likesRaw, hostName, guestName, whenUtc, DateTime.UtcNow);
	                        var computedFmt = LikesCalculator.ToCompact(computed, CultureInfo.InvariantCulture);

							var comments = 0;

                            if (likesandvotes != null && likesandvotes.Count >= 11)
                            {
                                items.Add(new TableDataItem(
                                    "flag",
                                    backgroundtipcolor,
                                    time ?? "",
									teamone,
                                    //renameTeam.renameTeamNameToFitDisplayLabel(teamone ?? ""),
                                    teamonescore,
                                    teamtwoscore,
									teamtwo,
                                    //renameTeam.renameTeamNameToFitDisplayLabel(teamtwo ?? ""),
                                    tip?.InnerText,
                                    likebuttonimage,
                                    dislikebuttonimage,
                                    likepositive,
                                    likenegative,
									computed,
									computedFmt,
									comments,
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
                                    AppColors.LightGray,
									null,
									false,
									AppColors.LightGray
                                ));
                            }
							
                        }

                        var groupImage = group.Descendants("img").FirstOrDefault()?.Attributes["src"].Value;
						var groupName  = group.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "name")?.InnerText.Trim();
						
						if (groupImage != null && groupName != null && items.Count > 0)
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
// ---------- LiveScores: models, storage, files ----------
public record LiveScoreItem(
    string Time,
    string Status,
    string HomeTeam,
    string HomeGoals,
    string AwayGoals,
    string AwayTeam,
	List<MatchAction> Action,
	string MatchID
);

public record LiveScoreGroup(
    string Competition,
    List<LiveScoreItem> Matches
);

public record LiveScoreDay(
    string Date,                 // "yyyy-MM-dd" in Europe/Brussels (server-local is ok if TZ is set)
    List<LiveScoreGroup> Groups
);

public sealed class LiveScoresStore
{
    private readonly ConcurrentDictionary<string, LiveScoreDay> _days = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveGate = new();
    public DateTimeOffset? LastSavedUtc { get; private set; }

    public void Set(LiveScoreDay day) => _days[day.Date] = day;
    public LiveScoreDay? Get(string date) => _days.TryGetValue(date, out var d) ? d : null;

    public (IReadOnlyList<LiveScoreDay> items, DateTimeOffset now) Export()
        => (_days.Values.OrderByDescending(x => x.Date).ToList(), DateTimeOffset.UtcNow);

    public void Import(IEnumerable<LiveScoreDay> items)
    {
        _days.Clear();
        foreach (var d in items) _days[d.Date] = d;
    }

    public IReadOnlyCollection<string> Dates() => _days.Keys.OrderByDescending(x => x).ToList();
    public void MarkSaved(DateTimeOffset ts) { lock (_saveGate) LastSavedUtc = ts; }

    /// Keep only the requested dates (by exact string key)
    public int ShrinkTo(IReadOnlyCollection<string> keep)
    {
        var set = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var key in _days.Keys)
        {
            if (!set.Contains(key))
            {
                if (_days.TryRemove(key, out _)) removed++;
            }
        }
        return removed;
    }
}

public static class LiveScoresFiles
{
    public const string File = "/var/lib/datasvc/livescores.json";

    public static async Task SaveAsync(LiveScoresStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var (items, now) = store.Export();
        var envelope = new
        {
            lastSavedUtc = now,
            days = items
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = false });
        var tmp = File + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, File, overwrite: true);

        try
		{
		    var gzPath = File + ".gz";
		    await using var input = System.IO.File.OpenRead(File);
		    await using var output = System.IO.File.Create(gzPath);
		    using var gz = new System.IO.Compression.GZipStream(
		        output,
		        System.IO.Compression.CompressionLevel.Fastest,
		        leaveOpen: false
		    );
		    await input.CopyToAsync(gz);
		}
		catch
		{
		    // Non-fatal: if gzip fails, the plain JSON is still available.
		}

        store.MarkSaved(now);
    }

    public static async Task<(List<LiveScoreDay> days, DateTimeOffset? lastSavedUtc)?> LoadAsync()
    {
        if (!System.IO.File.Exists(File)) return null;
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(File);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var last = root.TryGetProperty("lastSavedUtc", out var tsEl) && tsEl.ValueKind is not JsonValueKind.Null
                ? tsEl.GetDateTimeOffset()
                : (DateTimeOffset?)null;

            var days = new List<LiveScoreDay>();
            if (root.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in daysEl.EnumerateArray())
                {
                    var date = d.GetProperty("Date").GetString() ?? "";
                    var groups = new List<LiveScoreGroup>();
                    if (d.TryGetProperty("Groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in groupsEl.EnumerateArray())
                        {
                            var comp = g.GetProperty("Competition").GetString() ?? "";
                            var matches = new List<LiveScoreItem>();
                            if (g.TryGetProperty("Matches", out var msEl) && msEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var m in msEl.EnumerateArray())
                                {
									var actions = new List<MatchAction>();
									if (m.TryGetProperty("Action", out var actEl) && actEl.ValueKind == JsonValueKind.Array)
									{
									    foreach (var a in actEl.EnumerateArray())
									    {
									        var side   = (TeamSide)a.GetProperty("Side").GetInt32();
									        var kind   = (ActionKind)a.GetProperty("Kind").GetInt32();
									        int? minute = a.TryGetProperty("Minute", out var minEl) && minEl.ValueKind != JsonValueKind.Null
									            ? minEl.GetInt32()
									            : (int?)null;
									        var player = a.GetProperty("Player").GetString() ?? "";
									        actions.Add(new MatchAction(side, kind, minute, player));
									    }
									}
									
                                    matches.Add(new LiveScoreItem(
                                        m.GetProperty("Time").GetString() ?? "",
                                        m.GetProperty("Status").GetString() ?? "",
                                        m.GetProperty("HomeTeam").GetString() ?? "",
                                        m.GetProperty("HomeGoals").GetString() ?? "",
                                        m.GetProperty("AwayGoals").GetString() ?? "",
                                        m.GetProperty("AwayTeam").GetString() ?? "",
										actions,
										m.GetProperty("MatchID").GetString() ?? ""
                                    ));
                                }
                            }
                            groups.Add(new LiveScoreGroup(comp, matches));
                        }
                    }
                    days.Add(new LiveScoreDay(date, groups));
                }
            }

            return (days, last);
        }
        catch { return null; }
    }
}

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
			//return "http://" + s.TrimStart('/');
	
	    // Site-relative
	    var baseUri = new Uri("https://www.statarea.com/");
	    //var baseUri = new Uri("http://www.statarea.com/");
	    if (!s.StartsWith("/")) s = "/" + s;
	    return new Uri(baseUri, s).AbsoluteUri; // canonicalize
	}
}
internal static class DetailsMerge
{
    static string? PreferOldUnlessNullOrEmpty(string? oldValue, string? newValue)
    {
        if (!string.IsNullOrWhiteSpace(oldValue)) return oldValue;
        if (!string.IsNullOrWhiteSpace(newValue)) return newValue;
        return null;
    }

    public static DetailsRecord Merge(DetailsRecord? oldRec, DetailsRecord newRec)
    {
        // If we had nothing before, just take the fresh record as-is.
        if (oldRec is null) return newRec;

        var pOld = oldRec.Payload;
        var pNew = newRec.Payload;

        // Merge with your “prefer old unless empty” rule
        var mergedPayload = new DetailsPayload(
            PreferOldUnlessNullOrEmpty(pOld.TeamsInfoHtml,           pNew.TeamsInfoHtml),
            PreferOldUnlessNullOrEmpty(pOld.MatchBetweenHtml,        pNew.MatchBetweenHtml),
            PreferOldUnlessNullOrEmpty(pOld.TeamMatchesSeparateHtml, pNew.TeamMatchesSeparateHtml),
            PreferOldUnlessNullOrEmpty(pOld.LastTeamsMatchesHtml,    pNew.LastTeamsMatchesHtml),
            PreferOldUnlessNullOrEmpty(pOld.TeamsStatisticsHtml,     pNew.TeamsStatisticsHtml),
            PreferOldUnlessNullOrEmpty(pOld.TeamsBetStatisticsHtml,  pNew.TeamsBetStatisticsHtml),
            PreferOldUnlessNullOrEmpty(pOld.FactsHtml,               pNew.FactsHtml),
            PreferOldUnlessNullOrEmpty(pOld.TeamStandingsHtml,       pNew.TeamStandingsHtml)
        );

        // No-op? Keep the existing record (preserves LastUpdatedUtc).
        if (mergedPayload == pOld) return oldRec;

        // Real change: keep the merged payload, and use the fresh scrape’s timestamp.
        // (newRec.LastUpdatedUtc is set where the record is scraped.)
        return new DetailsRecord(newRec.Href, newRec.LastUpdatedUtc, mergedPayload);
    }
}

/*
internal static class DetailsMerge
{
	static string? PreferOldUnlessNullOrEmpty(string? oldValue, string? newValue)
	{
	    if (!string.IsNullOrWhiteSpace(oldValue)) return oldValue;
	    if (!string.IsNullOrWhiteSpace(newValue)) return newValue;

	    return null;
	}
    public static DetailsRecord Merge(DetailsRecord? oldRec, DetailsRecord newRec)
    {
        if (oldRec is null) return newRec;

        var pOld = oldRec.Payload;
        var pNew = newRec.Payload;

        var mergedPayload = new DetailsPayload(
		    PreferOldUnlessNullOrEmpty(pOld.TeamsInfoHtml,           pNew.TeamsInfoHtml),
		    PreferOldUnlessNullOrEmpty(pOld.MatchBetweenHtml,        pNew.MatchBetweenHtml),
		    PreferOldUnlessNullOrEmpty(pOld.TeamMatchesSeparateHtml, pNew.TeamMatchesSeparateHtml),
		    PreferOldUnlessNullOrEmpty(pOld.LastTeamsMatchesHtml,    pNew.LastTeamsMatchesHtml),
		    PreferOldUnlessNullOrEmpty(pOld.TeamsStatisticsHtml,     pNew.TeamsStatisticsHtml),
		    PreferOldUnlessNullOrEmpty(pOld.TeamsBetStatisticsHtml,  pNew.TeamsBetStatisticsHtml),
		    PreferOldUnlessNullOrEmpty(pOld.FactsHtml,               pNew.FactsHtml),
		    PreferOldUnlessNullOrEmpty(pOld.TeamStandingsHtml,       pNew.TeamStandingsHtml)
		);

        return new DetailsRecord(newRec.Href, DateTimeOffset.UtcNow, mergedPayload);
    }
}
*/

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
	/*
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
	*/
	public static async Task<IReadOnlyList<DetailsRecord>> LoadAsync()
	{
	    if (!System.IO.File.Exists(File)) return Array.Empty<DetailsRecord>();
	    try
	    {
	        var json = await System.IO.File.ReadAllTextAsync(File);
	        var doc = JsonDocument.Parse(json);
	        var items = doc.RootElement.GetProperty("items").Deserialize<List<DetailsRecord>>() ?? new();
	
	        var upgraded = new List<DetailsRecord>(items.Count);
	
	        foreach (var rec in items)
	        {
	            // Use existing values if present, fall back to empty string
	            var payload = new DetailsPayload(
	                TeamsInfoHtml:            rec.Payload?.TeamsInfoHtml            ?? string.Empty,
	                MatchBetweenHtml:         rec.Payload?.MatchBetweenHtml         ?? string.Empty,
	                TeamMatchesSeparateHtml:  rec.Payload?.TeamMatchesSeparateHtml  ?? string.Empty,
	                TeamsBetStatisticsHtml:   rec.Payload?.TeamsBetStatisticsHtml   ?? string.Empty,
	                FactsHtml:                rec.Payload?.FactsHtml                ?? string.Empty,
	                LastTeamsMatchesHtml:     rec.Payload?.LastTeamsMatchesHtml     ?? string.Empty,
	                TeamsStatisticsHtml:      rec.Payload?.TeamsStatisticsHtml      ?? string.Empty,
	                TeamStandingsHtml:        rec.Payload?.TeamStandingsHtml        ?? string.Empty
	            );
	
	            // Create a new record with the upgraded payload and preserve original timestamp + href
	            upgraded.Add(rec with { Payload = payload });
	        }
	
	        return upgraded;
	    }
	    catch (Exception ex)
	    {
	        Console.WriteLine($"[details] Failed to load or migrate details.json: {ex.Message}");
	        return Array.Empty<DetailsRecord>();
	    }
	}
}

public static class DetailsPerDateFiles
{
    public static string Dir  => "/var/lib/datasvc/details";
    public static string PathFor(DateOnly date) => System.IO.Path.Combine(Dir, $"{date:yyyy-MM-dd}.json");

    public static async Task SaveAsync(DateOnly date, object envelope)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = false });
        var path = PathFor(date);
        var tmp  = path + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, path, overwrite: true);

        // best-effort gzip, like elsewhere
        try
        {
            using var input = File.OpenRead(path);
            using var output = File.Create(path + ".gz");
            using var gz = new GZipStream(output, CompressionLevel.Fastest);
            await input.CopyToAsync(gz);
        }
        catch { /* non-fatal */ }

        // Enforce rolling window D±3 (7 files total), same pattern as parsed
        CleanupRetention(ScraperConfig.TodayLocal(), 3, 3); // mirrors parsed’s call site
    }

    public static void CleanupRetention(DateOnly center, int back, int ahead)
    {
        // Build keep set identical to parsed
        var keep = new HashSet<string>(
            ScraperConfig.DateWindow(center, back, ahead).Select(d => d.ToString("yyyy-MM-dd")),
            StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(Dir)) return;

        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var stem = Path.GetFileNameWithoutExtension(file); // yyyy-MM-dd
            if (!keep.Contains(stem))
            {
                try
                {
                    File.Delete(file);
                    var gz = file + ".gz";
                    if (File.Exists(gz)) File.Delete(gz);
                }
                catch { /* ignore */ }
            }
        }
    }
}

public sealed class DetailsRefreshService
{
    private readonly SnapshotPerDateStore _perDateStore;
    private readonly DetailsStore _details;
    private readonly DetailsScraperService _scraper;
    private readonly ILogger<DetailsRefreshService> _log;

    public DetailsRefreshService(
        SnapshotPerDateStore perDateStore,
        DetailsStore details,
        DetailsScraperService scraper,
        ILogger<DetailsRefreshService> log)
    {
        _perDateStore = perDateStore;
        _details = details;
        _scraper = scraper;
        _log = log;
    }

    // NEW: refresh details for all hrefs that appear in parsed today±3
    public async Task RefreshAllFromParsedWindowAsync(int back = 3, int ahead = 3, int maxConcurrency = 8, CancellationToken ct = default)
    {
        var center = ScraperConfig.TodayLocal();
        var dates = ScraperConfig.DateWindow(center, back, ahead);

        // 1) collect hrefs from every date that has a parsed snapshot
        var allHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dates)
        {
            if (!_perDateStore.TryGet(d, out var snap) || snap.Payload?.TableDataGroup is null) continue;

            foreach (var href in snap.Payload.TableDataGroup.SelectMany(g => g.Items).Select(i => i.Href))
            {
                if (string.IsNullOrWhiteSpace(href)) continue;
                allHrefs.Add(DetailsStore.Normalize(href));
            }
        }

        if (allHrefs.Count == 0)
        {
            _log.LogInformation("DetailsRefresh: No hrefs found in parsed window {Center}±({Back},{Ahead})", center, back, ahead);
            return;
        }

        _log.LogInformation("DetailsRefresh: {Count} distinct hrefs in parsed window", allHrefs.Count);

        // 2) fetch any missing or stale details
        var targets = allHrefs
            .Where(h => NeedsFetch(_details.Get(h)))
            .ToList();

        _log.LogInformation("DetailsRefresh: fetching {Count} hrefs (missing/stale)", targets.Count);

        var throttler = new SemaphoreSlim(maxConcurrency);
        var tasks = targets.Select(async h =>
        {
            await throttler.WaitAsync(ct);
            try
            {
                //var rec = await _scraper.FetchOneAsync(h, ct);   // prefer the injected instance
				//_details.Set(rec);
				//var rec = await DetailsScraperService.FetchOneAsync(h, ct);
				//_details.Set(rec);
				var existing = _details.Get(h);
		        var fresh    = await DetailsScraperService.FetchOneAsync(h, ct);
		        _details.Set(DetailsMerge.Merge(existing, fresh));

            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Fetch failed for {Href}", h);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);

        // 3) optional: materialize & save per-date aggregates after refresh
        foreach (var d in dates)
        {
            await GenerateAndSavePerDateAsync(d);
        }

        // 4) enforce on-disk retention for details per-date artifacts
        DetailsPerDateFiles.CleanupRetention(center, back, ahead);
    }

    private static bool IsIncomplete(DetailsRecord r)
	    => r.Payload.TeamsInfoHtml == null
	    || r.Payload.MatchBetweenHtml == null
	    || r.Payload.TeamMatchesSeparateHtml == null
	    || r.Payload.TeamsBetStatisticsHtml == null
	    || r.Payload.FactsHtml == null
	    || r.Payload.LastTeamsMatchesHtml == null
	    || r.Payload.TeamsStatisticsHtml == null
	    || r.Payload.TeamStandingsHtml == null;
	
	private static bool NeedsFetch(DetailsRecord? rec)
	    => rec is null || IsIncomplete(rec) || (DateTimeOffset.UtcNow - rec.LastUpdatedUtc) > TimeSpan.FromHours(3);


    // Reuse the same mapping the endpoint does, but as a helper so we can write files post-refresh
    private async Task GenerateAndSavePerDateAsync(DateOnly date)
    {
        if (!_perDateStore.TryGet(date, out var snap) || snap.Payload?.TableDataGroup is null) return;
		/*
		// BEFORE (collects from all groups)
		var hrefs = snap.Payload!.TableDataGroup
		    .SelectMany(g => g.Items)
		    .Select(i => i.Href)
		    .Where(h => !string.IsNullOrWhiteSpace(h))
		    .Distinct(StringComparer.OrdinalIgnoreCase)
		    .ToArray();
		
        var firstGroup = snap.Payload!.TableDataGroup.FirstOrDefault();
		var hrefs = (firstGroup?.Items ?? new ObservableCollection<TableDataItem>())
		    .Select(i => i.Href)
		    .Where(h => !string.IsNullOrWhiteSpace(h))
		    .Distinct(StringComparer.OrdinalIgnoreCase)
		    .ToArray();
		*/
		var hrefs = snap.Payload!.TableDataGroup
		    .SelectMany(g => g.Items)
		    .Select(i => i.Href)
		    .Where(h => !string.IsNullOrWhiteSpace(h))
		    .Distinct(StringComparer.OrdinalIgnoreCase)
		    .ToArray();

		// Stable index from parsed order
		var index = hrefs
		    .Select((h, i) => (h, i))
		    .ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);

		
        var records = hrefs
            .Select(h => _details.Get(h))
            .Where(r => r is not null)
            .Cast<DetailsRecord>()
            //.OrderByDescending(r => r.LastUpdatedUtc)
			.OrderBy(r => index[r.Href]) // <- stable
            .ToList();

        var byHref = records.ToDictionary(
            r => r.Href,
            r => AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
                    r,
                    preferTeamsInfoHtml:       false,
                    preferMatchBetweenHtml:    false,
                    preferSeparateMatchesHtml: false,
                    preferBetStatsHtml:        false,
                    preferFactsHtml:           false,
                    preferLastTeamsHtml:       false,
                    preferTeamsStatisticsHtml: false,
                    preferTeamStandingsHtml:   false),
            StringComparer.OrdinalIgnoreCase);

        var envelope = new
        {
            date = date.ToString("yyyy-MM-dd"),
            total = byHref.Count,
            //generatedUtc = DateTimeOffset.UtcNow,
            items = byHref
        };

        await DetailsPerDateFiles.SaveAsync(date, envelope);
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
	
	            //var rec = await FetchOneAsync(href, ct);
	            //_store.Set(rec);
				//var fresh   = await FetchOneAsync(href, ct);
				//var merged  = DetailsMerge.Merge(existing, fresh);
				//_store.Set(merged);
				var fresh   = await FetchOneWithRetryAsync(href, ct);
				var merged  = DetailsMerge.Merge(existing, fresh);
				_store.Set(merged);

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
	static bool IsTransient(Exception ex, CancellationToken ct)
	{
	    // If the caller actually cancelled, don't retry.
	    if (ct.IsCancellationRequested) return false;
	
	    // Typical transient network/timeout cases
	    if (ex is HttpRequestException) return true;
	    if (ex is IOException) return true;
	
	    // Timeout from linked CTS often shows up as TaskCanceledException/OperationCanceledException
	    if (ex is TaskCanceledException) return true;
	    if (ex is OperationCanceledException) return true;
	
	    // TLS/auth hiccups
	    if (ex.InnerException is System.Security.Authentication.AuthenticationException) return true;
	    if (ex.InnerException is System.Net.Sockets.SocketException) return true;
	
	    return false;
	}
	
	static TimeSpan ComputeDelay(int attempt)
	{
	    // attempt: 1..N
	    var baseMs = GetEnvInt("DETAILS_RETRY_BASE_DELAY_MS", 400); // default 400ms
	    var maxMs  = GetEnvInt("DETAILS_RETRY_MAX_DELAY_MS", 4000); // cap
	
	    var exp = baseMs * Math.Pow(2, attempt - 1);
	    var jitter = Random.Shared.Next(0, 250);
	
	    var ms = (int)Math.Min(maxMs, exp + jitter);
	    return TimeSpan.FromMilliseconds(ms);
	}
	
	public static async Task<DetailsRecord> FetchOneWithRetryAsync(string href, CancellationToken ct = default)
	{
	    var attempts = GetEnvInt("DETAILS_RETRIES", 3);
	
	    Exception? last = null;
	
	    for (int attempt = 1; attempt <= attempts; attempt++)
	    {
	        try
	        {
	            var rec = await FetchOneAsync(href, ct).ConfigureAwait(false);
	
	            // Optional: treat "obviously empty parse" as retryable.
	            // (If your ParseDetails sometimes returns blank sections when the site responds weirdly.)
	            var p = rec.Payload;
	            bool looksEmpty =
	                string.IsNullOrWhiteSpace(p.TeamsInfoHtml) ||
	                string.IsNullOrWhiteSpace(p.MatchBetweenHtml) ||
					string.IsNullOrWhiteSpace(p.TeamMatchesSeparateHtml) ||
	                string.IsNullOrWhiteSpace(p.LastTeamsMatchesHtml) ||
					string.IsNullOrWhiteSpace(p.TeamsStatisticsHtml) ||
	                string.IsNullOrWhiteSpace(p.TeamsBetStatisticsHtml) ||
					string.IsNullOrWhiteSpace(p.TeamStandingsHtml) ||
	                string.IsNullOrWhiteSpace(p.FactsHtml);
	
	            if (looksEmpty)
				{
					Console.WriteLine($"Retrying FetchOneAsync...");
					throw new InvalidDataException("Parsed details looks empty (teams/matchbetween/facts all null).");
				}
	                
	
	            return rec;
	        }
	        catch (Exception ex) when (IsTransient(ex, ct) && attempt < attempts)
	        {
	            last = ex;
	            var delay = ComputeDelay(attempt);
	            Console.WriteLine($"[details] retry {attempt}/{attempts} for {href}: {ex.GetType().Name}: {ex.Message} (delay {delay.TotalMilliseconds:0}ms)");
	            await Task.Delay(delay, ct).ConfigureAwait(false);
	        }
	        catch (Exception ex)
	        {
	            last = ex;
	            break;
	        }
	    }
	
	    throw last ?? new Exception("FetchOneWithRetryAsync failed with unknown error.");
	}

	public static async Task<DetailsRecord> FetchOneAsync(string href, CancellationToken ct = default)
	{
	    var abs = DetailsStore.Normalize(href);
	    Debug.WriteLine($"[details] fetching: {abs}");
	
	    // env-configurable per-request timeout (default 10s)
	    var timeoutSeconds = 10;
	    if (int.TryParse(Environment.GetEnvironmentVariable("DETAILS_TIMEOUT_SECONDS"), out var t) && t > 0)
	        timeoutSeconds = t;
	
	    var allowHttp = Environment.GetEnvironmentVariable("ALLOW_HTTP_STATAREA") == "1";
	
	    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
	    linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
	
	    // ---------- 1) Try HTTPS first ----------
	    try
	    {
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
	        return ParseDetails(abs, html);
	    }
	    catch (HttpRequestException ex)
	    {
	        // fall through to HTTP retry below
	    }
	
	    // ---------- 2) Retry over HTTP (strictly for statarea.com) ----------
	    var httpUri = ToHttp(abs);
	        using var req2 = new HttpRequestMessage(HttpMethod.Get, httpUri)
	        {
	            // Be pragmatic over plain HTTP: don't force h2
	            Version = HttpVersion.Version11,
	            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
	        };
	        req2.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
	        req2.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
	        req2.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	        req2.Headers.Referrer = new Uri("http://www.statarea.com/");
	
	        Console.WriteLine($"[details] TLS error; falling back to HTTP: {httpUri}");
	        using var res2 = await http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
	        res2.EnsureSuccessStatusCode();
	        var html2 = await res2.Content.ReadAsStringAsync(linkedCts.Token);
	        return ParseDetails(abs, html2);
	
	    // If we got here, rethrow the original error behavior
	    // (let upstream error handling/logging report it)
	    using (var req = new HttpRequestMessage(HttpMethod.Get, abs))
	    {
	        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
	        res.EnsureSuccessStatusCode();
	        var html = await res.Content.ReadAsStringAsync(linkedCts.Token);
	        return ParseDetails(abs, html);
	    }
	
	    // ---------- local helpers ----------
	    static bool IsStatarea(string url)
	    {
	        var host = new Uri(url).Host.ToLowerInvariant();
	        return host == "www.statarea.com" || host == "statarea.com";
	    }
	    static bool IsTlsError(HttpRequestException ex)
	        => ex.InnerException is System.Security.Authentication.AuthenticationException
	        || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
	        || ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);
	
	    static string ToHttp(string url)
	    {
	        var b = new UriBuilder(url) { Scheme = "http", Port = -1 };
	        return b.Uri.ToString();
	    }
	
	    static DetailsRecord ParseDetails(string abs, string html)
	    {
	        // ---- parse the sections (unchanged from your current logic) ----
	        var doc = new HtmlAgilityPack.HtmlDocument();
	        doc.LoadHtml(html);
	
	        static string? SectionFirst(HtmlDocument d, string cls) =>
	            d.DocumentNode.SelectSingleNode($"//div[contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')]")?.OuterHtml;
	
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
	
	        // your improved facts picker
	        static string? SectionFactsWithRows(HtmlDocument d)
	        {
	            const string ToLower = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
	            const string ToUpper = "abcdefghijklmnopqrstuvwxyz";
	            string tokenFacts = " contains(concat(' ', translate(normalize-space(@class), '" + ToLower + "', '" + ToUpper + "'), ' '), ' facts ') ";
	
	            var candidates = d.DocumentNode.SelectNodes(
	                "//*[self::div or self::section][" + tokenFacts + "] | " +
	                "//*[@id and contains(translate(@id,'" + ToLower + "','" + ToUpper + "'),'facts')]"
	            ) ?? new HtmlNodeCollection(null);
	
	            var anchor = d.DocumentNode.SelectSingleNode("//*[@name='linkmatchfacts' or @id='linkmatchfacts']");
	            if (anchor != null)
	            {
	                var near = anchor.SelectSingleNode(
	                    "following::*[self::div or self::section][" + tokenFacts + "][1]"
	                );
	                if (near != null) candidates.Add(near);
	            }
	
	            HtmlNode? best = null;
	            int bestScore = int.MinValue;
	
	            foreach (var n in candidates.Distinct())
	            {
	                var rows  = n.SelectNodes(".//*[contains(translate(@class,'" + ToLower + "','" + ToUpper + "'),'datarow')]")?.Count ?? 0;
	                var chart = n.SelectNodes(".//*[contains(translate(@class,'" + ToLower + "','" + ToUpper + "'),'stackedbarchart')]")?.Count ?? 0;
	                var len   = n.InnerHtml?.Length ?? 0;
	
	                int score = rows * 1_000_000 + chart * 10_000 + len;
	                if (score > bestScore) { bestScore = score; best = n; }
	            }
	
	            Debug.WriteLine($"[details] facts: candidates={candidates.Count}, pickedScore={bestScore}");
	            return best?.OuterHtml;
	        }
	
	        int mbDivs, mbRows;
	        var teamsInfoHtml        = SectionFirst(doc, "teamsinfo");
	        var lastTeamsMatchesHtml = SectionFirst(doc, "lastteamsmatches");
	        var teamsStatisticsHtml  = SectionFirst(doc, "teamsstatistics");
	        var teamsBetStatsHtml    = SectionFirst(doc, "teamsbetstatistics");
	        var factsHtml            = SectionFactsWithRows(doc);
	        var teamMatchesSeparateHtml = SectionFirst(doc, "lastteamsmatches");
	        var matchBetweenHtml     = SectionMatchBetweenFilled(doc, out mbDivs, out mbRows);
	        Debug.WriteLine($"[details] matchbtwteams: found {mbDivs} block(s); picked block with {mbRows} row(s)");
	        var teamStandingsHtml    = SectionFirst(doc, "teamstandings");
	
	        var payload = new DetailsPayload(
	            TeamsInfoHtml:            teamsInfoHtml,
	            MatchBetweenHtml:         matchBetweenHtml,
	            TeamMatchesSeparateHtml:  teamMatchesSeparateHtml,
	            LastTeamsMatchesHtml:     lastTeamsMatchesHtml,
	            TeamsStatisticsHtml:      teamsStatisticsHtml,
	            TeamsBetStatisticsHtml:   teamsBetStatsHtml,
	            FactsHtml:                factsHtml,
	            TeamStandingsHtml:        teamStandingsHtml
	        );
	
	        return new DetailsRecord(abs, DateTimeOffset.UtcNow, payload);
	    }
	}

	/*
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
	    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,//*;q=0.8");
	    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	    req.Headers.Referrer = new Uri("https://www.statarea.com/");
		//req.Headers.Referrer = new Uri("http://www.statarea.com/");
	
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

		var teamStandingsHtml = SectionFirst(doc, "teamstandings"); // NEW
		
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
	*/
}

public sealed class DetailsRefreshJob : BackgroundService
{
    private readonly DetailsScraperService _svc;
    private readonly DetailsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeZoneInfo _tz;
	private readonly DetailsRefreshService _refresher; // <— add

    public DetailsRefreshJob(DetailsScraperService svc, DetailsStore store, DetailsRefreshService refresher)
    {
        _svc = svc; 
        _store = store;
		_refresher = refresher; // <— add
        // Use local server timezone by default; allow override via env var TOP_OF_HOUR_TZ (e.g., "Europe/Brussels")
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load previous cache on startup
        var prev = await DetailsFiles.LoadAsync();
        if (prev.Count > 0) _store.Import(prev);

        // Let the main page warm up first
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }

        // Initial run
        await RunSafelyOnce("initial", stoppingToken);

        // Start hourly aligned loop in parallel
        _ = HourlyLoop(stoppingToken);

        // Every 5 minutes — keep existing cadence
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyOnce("tick", stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HourlyLoop(CancellationToken ct)
    {
        try
        {
            // Wait until the next top of the hour in the configured timezone
            while (!ct.IsCancellationRequested)
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
                int nextHour = nowLocal.Minute == 0 && nowLocal.Second == 0 ? nowLocal.Hour : nowLocal.Hour + 1;
                if (nextHour == 24) nextHour = 0;
                var nextTopLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nextHour, 0, 0, nowLocal.Offset);
                if (nextTopLocal <= nowLocal) nextTopLocal = nextTopLocal.AddHours(1);

                var delay = nextTopLocal - nowLocal;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await RunSafelyOnce("hourly", ct);

                // After the first aligned tick, continue hourly
                using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                while (await hourly.WaitForNextTickAsync(ct))
                {
                    await RunSafelyOnce("hourly", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(string reason, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            // Keep the fast “current day” details refresh…
            var r = await _svc.RefreshAllFromCurrentAsync(ct);
            Debug.WriteLine($"[details] {reason} current refreshed={r.Refreshed} skipped={r.Skipped} errors={r.Errors.Count}");

            // …and then refresh details for all hrefs that appear across parsed D±3:
            await _refresher.RefreshAllFromParsedWindowAsync(back: 3, ahead: 3, maxConcurrency: 8, ct: ct);
            Debug.WriteLine($"[details] {reason} window D±3 refresh completed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[details] {reason} failed: {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
// ---- Trade Signal types & utils ----
public sealed class TradeSignal
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("qty")]
    public string? Qty { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    // Accepts ISO-8601, Unix seconds, or Unix milliseconds (e.g., TradingView {{timenow}})
    [JsonPropertyName("trigger_time")]
    public string? TriggerTime { get; set; }

    [JsonPropertyName("max_lag")]
    public string? MaxLag { get; set; }

    [JsonPropertyName("strategy_id")]
    public string? StrategyId { get; set; }
}

public record TradeSignalReceived(
    TradeSignal Payload,
    DateTimeOffset ReceivedUtc,
    double? LagSeconds,
    bool Accepted,
    string? Reason
);

public sealed class TradeSignalStore
{
    private readonly ConcurrentQueue<TradeSignalReceived> _q = new();

    public void Add(TradeSignalReceived r)
    {
        _q.Enqueue(r);
        while (_q.Count > 200 && _q.TryDequeue(out _)) { } // cap memory
    }

    public IReadOnlyList<TradeSignalReceived> Last(int n)
        => _q.Reverse().Take(n).ToList();
}

public static class TradeSignalUtils
{
    public static (DateTimeOffset? ts, double? lagSeconds) TryParseTriggerTime(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
            return (null, null);

        if (DateTimeOffset.TryParse(trigger, out var iso))
            return (iso, (DateTimeOffset.UtcNow - iso.ToUniversalTime()).TotalSeconds);

        if (long.TryParse(trigger, out var unix))
        {
            var isMillis = unix >= 1_000_000_000_000;
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(isMillis ? unix : unix * 1000L);
            return (ts, (DateTimeOffset.UtcNow - ts).TotalSeconds);
        }

        return (null, null);
    }

    public static int? TryParseInt(string? s) => int.TryParse(s, out var v) ? v : (int?)null;
}
