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
using Microsoft.AspNetCore.Http.Features;
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
using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;
using DataSvc.Top10;
using DataSvc.Tips;
using Google.Apis.Auth;
using DataSvc.Google;

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

// Trade Signal Webhook DI
builder.Services.AddSingleton<TradeSignalStore>();

// App services
builder.Services.AddSingleton<ResultStore>();
builder.Services.AddSingleton<ScraperService>();
builder.Services.AddHostedService<RefreshJob>();

builder.Services.AddDetailsServices();

builder.Services.AddParsedServices();

builder.Services.AddLiveScoresServices();

builder.Services.AddTop10Services();

builder.Services.AddTipsServices();

builder.Services.AddHostedService<OldDataCleanupJob >();

builder.Services.AddSingleton<GooglePlayClient>();

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
/*
builder.Services.AddCors(o => o.AddDefaultPolicy(p => 
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
*/
var app = builder.Build();

app.Services.WarmParsedSnapshotsFromDisk();

app.UseResponseCompression();

app.UseRouting(); 

//app.UseCors("AllowAll");


app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapParsedEndpoints();
app.MapDetailsEndpoints();
app.MapLiveScoresEndpoints();
app.MapTop10Endpoints();
app.MapTipsEndpoints();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/v1/iap/google/ping"))
    {
        Console.WriteLine($"PING REQ: Method={ctx.Request.Method}, Path={ctx.Request.Path}, ContentType={ctx.Request.ContentType}, Length={ctx.Request.ContentLength}, Headers={string.Join(";", ctx.Request.Headers.Select(h => $"{h.Key}:{h.Value}"))}");
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/v1/iap/google/verify-consumable"))
    {
        var f = ctx.Features.Get<IHttpRequestFeature>();
        Console.WriteLine(
            $"RAW  method={f?.Method} rawTarget={f?.RawTarget} " +
            $"path={ctx.Request.Path} ct={ctx.Request.ContentType} cl={ctx.Request.ContentLength} " +
            $"x-http-method-override={ctx.Request.Headers["X-HTTP-Method-Override"].ToString()}"
        );
    }
    await next();
});



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
			|| path.StartsWith("/app-version") 
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

// === App Version editor (HTML + write) ===
app.MapGet("/app-version", async ctx =>
{
    var filePath = Environment.GetEnvironmentVariable("APP_VERSION_FILE")
                 ?? "/var/lib/datasvc/app-version.txt";

    string current = System.IO.File.Exists(filePath)
        ? await System.IO.File.ReadAllTextAsync(filePath)
        : "AI Scores Predictor, v?.?.?, code ?, date ?";

    // Gentle parse: Product, vX.Y.Z, code N, date dd/MM/yyyy
    string product = "AI Scores Predictor";
    string version = "v1.0.0";
    string buildCode = "0";
    string date = DateTime.UtcNow.ToString("dd/MM/yyyy");

    try
    {
        // Expected format: "AI Scores Predictor, v1.0.2, code 33, date 03/01/2026"
        // Split by commas to stay resilient
        var parts = current.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1) product = parts[0];
        if (parts.Length >= 2) version = parts[1];                     // e.g. "v1.2.3"
        if (parts.Length >= 3) buildCode = parts[2].Replace("code","", StringComparison.OrdinalIgnoreCase).Trim();
        if (parts.Length >= 4) date = parts[3].Replace("date","", StringComparison.OrdinalIgnoreCase).Trim();
    }
    catch { /* keep defaults */ }

    var html = $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <title>Edit App Version</title>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <style>
    body{{font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial,sans-serif;max-width:560px;margin:3rem auto;padding:0 1rem}}
    label{{display:block;margin:.5rem 0 .25rem}}
    input{{width:100%;padding:.6rem;border:1px solid #ccc;border-radius:.5rem}}
    .row{{display:grid;grid-template-columns:1fr 1fr;gap:.75rem}}
    button{{margin-top:1rem;padding:.7rem 1rem;border:0;border-radius:.6rem;background:#0a7cff;color:#fff;font-weight:600;cursor:pointer}}
    .muted{{color:#666;margin-top:.75rem;font-size:.9em}}
  </style>
</head>
<body>
  <h1>Edit app version</h1>
  <form method=""post"" action=""/app-version"" novalidate>
    <label>Product</label>
    <input name=""product"" value=""{WebUtility.HtmlEncode(product)}"" required>

    <div class=""row"">
      <div>
        <label>Version (e.g. v1.0.2)</label>
		<input name=""version"" value=""{WebUtility.HtmlEncode(version)}"" pattern=""[vV]?\d+(\.\d+){2}"" title=""Use v1.2.3 or 1.2.3"" required>
      </div>
      <div>
        <label>Build code (integer)</label>
        <input name=""buildCode"" value=""{WebUtility.HtmlEncode(buildCode)}"" pattern=""\\d+"" required>
      </div>
    </div>

    <label>Date (dd/MM/yyyy)</label>
    <input name=""date"" value=""{WebUtility.HtmlEncode(date)}"" pattern=""\\d{{2}}/\\d{{2}}/\\d{{4}}"" required>

    <label>Code</label>
    <input name=""code"" type=""password"" autocomplete=""one-time-code"" required>

    <button type=""submit"">Save</button>
    <p class=""muted"">Saves to {WebUtility.HtmlEncode(filePath)} and updates <code>/app-version.txt</code>.</p>
  </form>
  <p class=""muted"">Current line:<br><code>{WebUtility.HtmlEncode(current)}</code></p>
</body>
</html>";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html);
});

app.MapPost("/app-version", async ctx =>
{
    var form = await ctx.Request.ReadFormAsync();
    var product   = (form["product"].ToString()   ?? "").Trim();
    var version   = (form["version"].ToString()   ?? "").Trim();
    var buildCode = (form["buildCode"].ToString() ?? "").Trim();
    var dateStr   = (form["date"].ToString()      ?? "").Trim();
    var code      = (form["code"].ToString()      ?? "");

    var expected = Environment.GetEnvironmentVariable("APP_VERSION_CODE");
    if (string.IsNullOrEmpty(expected) || !string.Equals(code, expected, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("Invalid code.");
        return;
    }

    // Validate fields
    if (!Regex.IsMatch(version, @"^[vV]?\d+(\.\d+){2}$"))
	{
	    ctx.Response.StatusCode = 400;
	    await ctx.Response.WriteAsync("Version must look like 1.2.3 or v1.2.3");
	    return;
	}
	// Normalize to always write with leading 'v'
	if (!version.StartsWith('v') && !version.StartsWith('V'))
	    version = "v" + version;
    if (!Regex.IsMatch(buildCode, @"^\d+$"))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Build code must be an integer.");
        return;
    }
    if (!DateTime.TryParseExact(dateStr, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Date must be dd/MM/yyyy.");
        return;
    }

    var filePath = Environment.GetEnvironmentVariable("APP_VERSION_FILE")
                 ?? "/var/lib/datasvc/app-version.txt";

    var line = $"{product}, {version}, code {buildCode}, date {dateStr}\n";
    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    await System.IO.File.WriteAllTextAsync(filePath, line, Encoding.UTF8);

    // Redirect back to GET page
    ctx.Response.Redirect("/app-version", permanent: false);
});

// Optional raw writer for scripts: PUT /app-version.txt?code=XXXX (body is whole line)
app.MapMethods("/app-version.txt", new[] { "PUT" }, async (HttpContext ctx) =>
{
    var code = ctx.Request.Query["code"].ToString();
    var expected = Environment.GetEnvironmentVariable("APP_VERSION_CODE");

    if (string.IsNullOrEmpty(expected) || !string.Equals(code, expected, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var line = (await sr.ReadToEndAsync()).Trim();
    if (string.IsNullOrWhiteSpace(line))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "empty body" });
        return;
    }

    var filePath = Environment.GetEnvironmentVariable("APP_VERSION_FILE")
                 ?? "/var/lib/datasvc/app-version.txt";

    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    await System.IO.File.WriteAllTextAsync(filePath, line + "\n", Encoding.UTF8);

    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
});



app.MapGet("/app-ads.txt", async ctx =>
{
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    // cache a bit (adjust as you like)
    //ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
	ctx.Response.Headers["Cache-Control"] = "no-store";
    //await ctx.Response.WriteAsync("google.com, pub-3388518972215761, DIRECT, f08c47fec0942fa0");
	//await ctx.Response.WriteAsync("facebook.com, 2656320454390110, DIRECT, c3e20eee3f780d68");
	await ctx.Response.WriteAsync("google.com, pub-3388518972215761, DIRECT, f08c47fec0942fa0\n");
    await ctx.Response.WriteAsync("facebook.com, 2656320454390110, DIRECT, c3e20eee3f780d68\n");
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

// Saved JSON snapshot (full)
//app.MapGet("/data/snapshot", () => Results.File("/var/lib/datasvc/latest.json", "application/json"));

// Manual refresh (main page)
app.MapPost("/data/refresh", async ([FromServices] ScraperService svc) =>
{
    var snap = await svc.FetchAndStoreAsync();
    return Results.Json(new { ok = snap.Ready, snap.LastUpdatedUtc, snap.Error });
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

// Map MVC controllers (AuthController)
app.MapControllers();
app.Run();

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

                            //if (hrefs != null) hrefs = hrefs.Replace(" ", "%20");
							if (!string.IsNullOrWhiteSpace(hrefs))
							{
							    hrefs = hrefs.Replace(" ", "%20");
							    hrefs = DetailsStore.Normalize(hrefs);
							}

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
									likepositive,
                                    //renameTeam.renameTeamNameToFitDisplayLabel(teamtwo ?? ""),
                                    tip?.InnerText,
                                    //likebuttonimage,
                                    //dislikebuttonimage,
                                    //likenegative,
									computed,
									computedFmt,
									comments,
									/*
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
									*/
                                    //"Beta",
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
    string MatchID,
    HalfTimeScore? HalfTime = null
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
