using System.Net;
using System.Text;
using System.Collections.Concurrent;

// -------------------- Models --------------------

public record ClubEloRank(
    int Rank,
    string Club,
    string Country,
    int Level,
    double Elo,
    string From,
    string To
);

public record ClubEloFixture(
    string Date,                // yyyy-MM-dd
    string Country,
    string Home,
    string Away,
    double HomeWin,
    double Draw,
    double AwayWin,
    string? MostLikelyScore,
    double MostLikelyScoreProb,
    IReadOnlyDictionary<string, double> GoalDiff,   // "GD:-1" etc
    IReadOnlyDictionary<string, double> Scoreline   // "R:1-0" etc
);

// -------------------- Store + Files --------------------

public sealed class ClubEloStore
{
    private readonly object _lock = new();

    private List<ClubEloRank>? _currentRanks;
    private readonly ConcurrentDictionary<string, List<ClubEloFixture>> _fixturesByDate =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastRanksFetchUtc { get; private set; }
    public DateTimeOffset? LastFixturesFetchUtc { get; private set; }

    public List<ClubEloRank>? GetCurrentRanks()
    {
        lock (_lock) return _currentRanks;
    }

    public List<ClubEloFixture>? GetFixturesForDate(string date)
        => _fixturesByDate.TryGetValue(date, out var list) ? list : null;

    public void SetRanks(List<ClubEloRank> ranks, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            _currentRanks = ranks;
            LastRanksFetchUtc = nowUtc;
        }
    }

    public void SetFixturesWindow(Dictionary<string, List<ClubEloFixture>> byDate, DateTimeOffset nowUtc)
    {
        _fixturesByDate.Clear();
        foreach (var kv in byDate) _fixturesByDate[kv.Key] = kv.Value;
        LastFixturesFetchUtc = nowUtc;
    }

    public void ShrinkFixturesTo(HashSet<string> keepDates)
    {
        foreach (var key in _fixturesByDate.Keys)
        {
            if (!keepDates.Contains(key))
                _fixturesByDate.TryRemove(key, out _);
        }
    }

    public (List<ClubEloRank>? ranks, Dictionary<string, List<ClubEloFixture>> fixturesByDate, DateTimeOffset nowUtc) Export()
    {
        var now = DateTimeOffset.UtcNow;
        List<ClubEloRank>? ranksCopy;
        Dictionary<string, List<ClubEloFixture>> fxCopy = new(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            ranksCopy = _currentRanks is null ? null : new List<ClubEloRank>(_currentRanks);
        }

        foreach (var kv in _fixturesByDate)
            fxCopy[kv.Key] = new List<ClubEloFixture>(kv.Value);

        return (ranksCopy, fxCopy, now);
    }

    public void Import(List<ClubEloRank>? ranks, DateTimeOffset? ranksUtc, Dictionary<string, List<ClubEloFixture>> fixtures, DateTimeOffset? fixturesUtc)
    {
        lock (_lock)
        {
            _currentRanks = ranks;
            LastRanksFetchUtc = ranksUtc;
        }

        _fixturesByDate.Clear();
        foreach (var kv in fixtures) _fixturesByDate[kv.Key] = kv.Value;

        LastFixturesFetchUtc = fixturesUtc;
    }
}

public static class ClubEloFiles
{
    private const string Dir = "/var/lib/datasvc/clubelo";
    public const string CurrentFile = Dir + "/current.json";
    public const string FixturesFile = Dir + "/fixtures.json";

    public static async Task SaveAsync(ClubEloStore store)
    {
        Directory.CreateDirectory(Dir);

        var (ranks, fixturesByDate, nowUtc) = store.Export();

        // current
        {
            var envelope = new { lastSavedUtc = nowUtc, ranks };
            await AtomicWriteJsonAsync(CurrentFile, envelope);
        }

        // fixtures
        {
            var days = fixturesByDate
                .OrderBy(kv => kv.Key)
                .Select(kv => new { Date = kv.Key, Items = kv.Value })
                .ToList();

            var envelope = new { lastSavedUtc = nowUtc, days };
            await AtomicWriteJsonAsync(FixturesFile, envelope);
        }
    }

    public static async Task LoadAsync(ClubEloStore store)
    {
        // current
        List<ClubEloRank>? ranks = null;
        DateTimeOffset? ranksUtc = null;

        if (File.Exists(CurrentFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(CurrentFile));
                var root = doc.RootElement;
                ranksUtc = root.TryGetProperty("lastSavedUtc", out var tsEl) ? tsEl.GetDateTimeOffset() : null;

                if (root.TryGetProperty("ranks", out var ranksEl) && ranksEl.ValueKind == JsonValueKind.Array)
                {
                    ranks = new();
                    foreach (var r in ranksEl.EnumerateArray())
                    {
                        ranks.Add(new ClubEloRank(
                            r.GetProperty("Rank").GetInt32(),
                            r.GetProperty("Club").GetString() ?? "",
                            r.GetProperty("Country").GetString() ?? "",
                            r.GetProperty("Level").GetInt32(),
                            r.GetProperty("Elo").GetDouble(),
                            r.GetProperty("From").GetString() ?? "",
                            r.GetProperty("To").GetString() ?? ""
                        ));
                    }
                }
            }
            catch { /* ignore */ }
        }

        // fixtures
        var fixtures = new Dictionary<string, List<ClubEloFixture>>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? fixturesUtc = null;

        if (File.Exists(FixturesFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(FixturesFile));
                var root = doc.RootElement;
                fixturesUtc = root.TryGetProperty("lastSavedUtc", out var tsEl) ? tsEl.GetDateTimeOffset() : null;

                if (root.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in daysEl.EnumerateArray())
                    {
                        var date = d.GetProperty("Date").GetString() ?? "";
                        var items = new List<ClubEloFixture>();

                        if (d.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var it in itemsEl.EnumerateArray())
                            {
                                items.Add(JsonSerializer.Deserialize<ClubEloFixture>(it.GetRawText())!);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(date))
                            fixtures[date] = items;
                    }
                }
            }
            catch { /* ignore */ }
        }

        store.Import(ranks, ranksUtc, fixtures, fixturesUtc);
    }

    private static async Task AtomicWriteJsonAsync(string file, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, file, overwrite: true);

        // optional gzip next to it (same pattern as your livescores)
        try
        {
            var gzPath = file + ".gz";
            await using var input = File.OpenRead(file);
            await using var output = File.Create(gzPath);
            using var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: false);
            await input.CopyToAsync(gz);
        }
        catch { /* non-fatal */ }
    }
}

// -------------------- Scraper --------------------

public sealed class ClubEloScraperService
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromMinutes(3) // ClubElo can be slow/variable
    };

    public async Task<List<ClubEloRank>> FetchCurrentRanksAsync(CancellationToken ct)
    {
        var csv = await GetStringWithRetriesAsync("http://api.clubelo.com/", ct);
        return ClubEloCsv.ParseRanks(csv);
    }

    public async Task<List<ClubEloFixture>> FetchFixturesAsync(CancellationToken ct)
    {
        // ClubElo is often used as /Fixtures, but many clients also use /fixtures.
        var csv = await GetStringWithRetriesAsync("http://api.clubelo.com/Fixtures", ct);
        return ClubEloCsv.ParseFixtures(csv);
    }

    private static async Task<string> GetStringWithRetriesAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "DataSvc/1.0 (+https://scorespredict.com)");
                req.Headers.TryAddWithoutValidation("Accept", "text/csv,*/*");
                req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }
        throw new InvalidOperationException($"Failed GET {url}", last);
    }
}

// -------------------- Background refresh job --------------------

public sealed class ClubEloRefreshJob : BackgroundService
{
    private readonly ClubEloScraperService _svc;
    private readonly ClubEloStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClubEloRefreshJob(ClubEloScraperService svc, ClubEloStore store)
    {
        _svc = svc;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm from disk
        await ClubEloFiles.LoadAsync(_store);

        // Do an initial refresh
        await RunOnce(stoppingToken);

        // Periodic loop: ranks hourly, fixtures every 12h; also keeps window in sync when the day rolls
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnce(stoppingToken);
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;

            // 1) Refresh ranks if stale (> 1h)
            var ranksStale = _store.LastRanksFetchUtc is null || (nowUtc - _store.LastRanksFetchUtc.Value) > TimeSpan.FromHours(1);
            if (ranksStale)
            {
                var ranks = await _svc.FetchCurrentRanksAsync(ct);
                _store.SetRanks(ranks, nowUtc);
            }

            // 2) Refresh fixtures if stale (> 12h)
            var fixturesStale = _store.LastFixturesFetchUtc is null || (nowUtc - _store.LastFixturesFetchUtc.Value) > TimeSpan.FromHours(12);
            if (fixturesStale)
            {
                var all = await _svc.FetchFixturesAsync(ct);

                var center = ScraperConfig.TodayLocal();
                var keep = ScraperConfig.DateWindow(center, back: 3, ahead: 3)
                    .Select(d => d.ToString("yyyy-MM-dd"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var byDate = all
                    .Where(f => keep.Contains(f.Date))
                    .GroupBy(f => f.Date, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                _store.SetFixturesWindow(byDate, nowUtc);
            }

            // 3) Always enforce rolling window for fixtures (in case only the day changed)
            {
                var center = ScraperConfig.TodayLocal();
                var keep = ScraperConfig.DateWindow(center, back: 3, ahead: 3)
                    .Select(d => d.ToString("yyyy-MM-dd"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _store.ShrinkFixturesTo(keep);
            }

            await ClubEloFiles.SaveAsync(_store);
        }
        catch
        {
            // Serve last good cache; next tick will try again.
        }
        finally
        {
            _gate.Release();
        }
    }
}

// -------------------- CSV parsing --------------------

public static class ClubEloCsv
{
    public static List<ClubEloRank> ParseRanks(string csv)
    {
        using var sr = new StringReader(csv);
        var headerLine = sr.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return new();

        var header = ParseRow(headerLine);
        // Expect: Rank,Club,Country,Level,Elo,From,To
        var list = new List<ClubEloRank>();

        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = ParseRow(line);
            if (cols.Count < 7) continue;

            int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank);
            int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
            double.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var elo);

            list.Add(new ClubEloRank(
                rank,
                cols[1],
                cols[2],
                level,
                elo,
                cols[5],
                cols[6]
            ));
        }

        // sort by rank if present
        return list.OrderBy(r => r.Rank == 0 ? int.MaxValue : r.Rank).ToList();
    }

    public static List<ClubEloFixture> ParseFixtures(string csv)
    {
        using var sr = new StringReader(csv);
        var headerLine = sr.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return new();

        var header = ParseRow(headerLine);

        int idxDate = header.FindIndex(h => h.Equals("Date", StringComparison.OrdinalIgnoreCase));
        int idxCountry = header.FindIndex(h => h.Equals("Country", StringComparison.OrdinalIgnoreCase));
        int idxHome = header.FindIndex(h => h.Equals("Home", StringComparison.OrdinalIgnoreCase));
        int idxAway = header.FindIndex(h => h.Equals("Away", StringComparison.OrdinalIgnoreCase));

        if (idxDate < 0 || idxCountry < 0 || idxHome < 0 || idxAway < 0) return new();

        var gdIdx = header
            .Select((h, i) => (h, i))
            .Where(x => x.h.StartsWith("GD:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rIdx = header
            .Select((h, i) => (h, i))
            .Where(x => x.h.StartsWith("R:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var list = new List<ClubEloFixture>();

        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = ParseRow(line);
            if (cols.Count != header.Count) continue;

            var date = cols[idxDate];
            var country = cols[idxCountry];
            var home = cols[idxHome];
            var away = cols[idxAway];

            var gd = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (h, i) in gdIdx)
            {
                if (double.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    gd[h] = v;
            }

            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (h, i) in rIdx)
            {
                if (double.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    scores[h] = v;
            }

            // Compute W/D/L from scoreline probabilities
            double hw = 0, dr = 0, aw = 0;
            string? bestScore = null;
            double bestP = -1;

            foreach (var kv in scores)
            {
                var key = kv.Key;     // "R:1-0"
                var p = kv.Value;

                if (p > bestP) { bestP = p; bestScore = key; }

                var s = key.AsSpan();
                if (!s.StartsWith("R:", StringComparison.OrdinalIgnoreCase)) continue;
                s = s.Slice(2); // "1-0"
                var dash = s.IndexOf('-');
                if (dash <= 0) continue;

                if (!int.TryParse(s.Slice(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hg)) continue;
                if (!int.TryParse(s.Slice(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ag)) continue;

                if (hg > ag) hw += p;
                else if (hg == ag) dr += p;
                else aw += p;
            }

            list.Add(new ClubEloFixture(
                date, country, home, away,
                hw, dr, aw,
                bestScore is null ? null : bestScore.Replace("R:", "", StringComparison.OrdinalIgnoreCase),
                bestP < 0 ? 0 : bestP,
                gd, scores
            ));
        }

        return list;
    }

    private static List<string> ParseRow(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    private static int FindIndex(this List<string> list, Func<string, bool> pred)
    {
        for (int i = 0; i < list.Count; i++) if (pred(list[i])) return i;
        return -1;
    }
}
