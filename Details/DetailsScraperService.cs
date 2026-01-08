// Details/DetailsScraperService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.SocketsHttpHandler; // only if you reference SocketsHttpHandler explicitly
using System.Threading;
using System.Threading.Tasks;

namespace DataSvc.Details
{
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
    }
}
