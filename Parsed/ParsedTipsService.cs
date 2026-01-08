// Parsed/ParsedTipsService.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO; // <-- needed for File/FileInfo/Directory

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

namespace DataSvc.Parsed;

public sealed class ParsedTipsService
{
    private readonly LiveScoresStore _live;
    private readonly ClubEloStore _clubEloStore;
    private readonly DetailsStore _detailsStore;

    public ParsedTipsService(LiveScoresStore live, ClubEloStore clubEloStore, DetailsStore detailsStore)
    {
        _live = live;
        _clubEloStore = clubEloStore;
        _detailsStore = detailsStore;
    }

    private static bool IsDetailsDtoEmpty(DetailsItemDto? d)
    {
        if (d is null) return true;
        // Treat the DTO as "empty" if none of the parsed sections are present.
        // (TipAnalyzer relies on these.)
        var chartsEmpty = d.BarCharts is null || d.BarCharts.Count == 0;
        return d.TeamsInfo is null
            && d.MatchFacts is null
            && d.TeamStandings is null
            && d.MatchDataBetween is null
            && chartsEmpty;
    }

    private static bool PayloadLooksEmpty(DetailsPayload? p)
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

    /// <summary>
    /// Fallback path when the per-date details file is missing the href OR the DTO is empty:
    /// try to resolve via the same logic as GET /data/details/item?href= (cache → fetch+store → map).
    /// </summary>
    private async Task<DetailsItemDto?> TryGetDetailsDtoByHrefAsync(string href, CancellationToken ct)
    {
        var norm = DetailsStore.Normalize(href);
        var rec = _detailsStore.Get(norm);

        // If missing or obviously empty, scrape once (with retry), merge, store, and persist.
        if (rec is null || PayloadLooksEmpty(rec.Payload))
        {
            try
            {
                var existing = rec;
                var fresh = await DetailsScraperService.FetchOneWithRetryAsync(norm, ct).ConfigureAwait(false);
                var merged = DetailsMerge.Merge(existing, fresh);
                _detailsStore.Set(merged);
                await DetailsFiles.SaveAsync(_detailsStore).ConfigureAwait(false);
                rec = merged;
            }
            catch
            {
                // best-effort fallback: if we had *any* cached record, keep going;
                // otherwise signal failure with null.
                if (rec is null) return null;
            }
        }

        if (rec is null) return null;

        // Map using the same mapping used by /data/details/allhrefs and /data/details/item.
        var mapped = AllhrefsMapper.MapDetailsRecordToAllhrefsItem(
            rec,
            preferTeamsInfoHtml: false,
            preferMatchBetweenHtml: false,
            preferSeparateMatchesHtml: false,
            preferBetStatsHtml: false,
            preferFactsHtml: false,
            preferLastTeamsHtml: false,
            preferTeamsStatisticsHtml: false,
            preferTeamStandingsHtml: false);

        // MapDetailsRecordToAllhrefsItem may return an anonymous object; deserialize defensively.
        if (mapped is DetailsItemDto dto) return dto;
        try
        {
            var json = JsonSerializer.Serialize(mapped, _json);
            return JsonSerializer.Deserialize<DetailsItemDto>(json, _json);
        }
        catch
        {
            return null;
        }
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
                    MatchID = m.MatchID,
                    //HalfTimeHomeGoals = m.HalfTime?.host.ToString(),
                    //HalfTimeAwayGoals = m.HalfTime?.guest.ToString(),
                    HalfTimeHomeGoals = m.HalfTime?.Host.ToString(),
                    HalfTimeAwayGoals = m.HalfTime?.Guest.ToString()
                }).ToList() ?? new List<LiveScoreItemResponse>()
            }).ToList() ?? new List<LiveScoreGroupResponse>()
        };
    }

    private static DetailsItemDto TrimForVm(DetailsItemDto src)
    {
        return new DetailsItemDto
        {
            TeamsInfo = src.TeamsInfo,
            MatchFacts = src.MatchFacts,
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
        int detailsFallbackUsed = 0, detailsFallbackFailed = 0, detailsDtoEmpty = 0;
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
                var hasDetail = detailsByHref.TryGetValue(normHref, out var detailDto) && detailDto is not null;

                // If missing OR empty, try fallbacks:
                //  1) alternate scheme key (http/https mismatch)
                //  2) the same logic as GET /data/details/item?href= (cache → fetch+store → map)
                if (!hasDetail || IsDetailsDtoEmpty(detailDto))
                {
                    // detect likely “looks same but differs” cases:
                    var https = ForceScheme(normHref, "https");
                    var http = ForceScheme(normHref, "http");

                    if (!hasDetail)
                    {
                        if (detailsByHref.TryGetValue(https, out var altHttps) && altHttps is not null && !IsDetailsDtoEmpty(altHttps))
                        {
                            detailDto = altHttps;
                            detailsByHref[normHref] = altHttps; // alias for future lookups
                            hasDetail = true;
                            schemeMismatch++;
                        }
                        else if (detailsByHref.TryGetValue(http, out var altHttp) && altHttp is not null && !IsDetailsDtoEmpty(altHttp))
                        {
                            detailDto = altHttp;
                            detailsByHref[normHref] = altHttp;
                            hasDetail = true;
                            schemeMismatch++;
                        }
                    }

                    // If still missing/empty, fetch via href (fallback endpoint logic)
                    if (!hasDetail || IsDetailsDtoEmpty(detailDto))
                    {
                        var fallback = await TryGetDetailsDtoByHrefAsync(normHref, ct).ConfigureAwait(false);
                        if (fallback is not null && !IsDetailsDtoEmpty(fallback))
                        {
                            detailDto = fallback;
                            detailsByHref[normHref] = fallback;
                            hasDetail = true;
                            detailsFallbackUsed++;
                        }
                        else
                        {
                            detailsFallbackFailed++;
                        }
                    }
                }

                // If after all fallbacks we still have nothing useful, skip the item.
                if (!hasDetail || IsDetailsDtoEmpty(detailDto))
                {
                    noDetail++;
                    if (hasDetail && IsDetailsDtoEmpty(detailDto)) detailsDtoEmpty++;

                    if (debugTips && samples.Count < 25)
                        samples.Add(new { reason = hasDetail ? "details-empty" : "no-details", raw = href, norm = normHref });

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
                int h2hEffMatches = 0;
                try
                {
                    analyzed++;

                    var hostSafe = item.HostTeam ?? string.Empty;
                    var guestSafe = item.GuestTeam ?? string.Empty;

                    EnsureEloIndex();

                    double? homeElo = null, awayElo = null;
                    if (TryGetElo(hostSafe, out var he)) homeElo = he;
                    if (TryGetElo(guestSafe, out var ae)) awayElo = ae;

                    (probs, _) = await Task.Run(
                        () => TipAnalyzer.Analyze(detailDto, hostSafe, guestSafe, item.Tip, null, null),
                        ct
                    ).ConfigureAwait(false);
                    (probsVIP, h2hEffMatches) = await Task.Run(
                        () => TipAnalyzer.Analyze(detailDto, hostSafe, guestSafe, item.Tip, homeElo, awayElo),
                        ct
                    ).ConfigureAwait(false);

                    item.MassH2H = h2hEffMatches; // <- whatever field/property you want
                    /*
                    var vip = TipAnalyzer.PickVip(
                        detailDto, hostSafe, guestSafe, probsVIP, homeElo, awayElo
                    );
                    item.VIPTip = vip.code;
                    */
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
                var backgroundTipColourVIPELO = item.BackgroundTipColourVIPELO;

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

                HalfTimeScore? halfTime = item.HalfTime; // keep existing unless we have a better value

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

                // Pick the same live dto you’re using for scoring
                var livePick = matched?.Item ?? live;

                // NEW: set halftime if available (both sides present)
                if (livePick?.HalfTimeHomeGoals is int htH && livePick?.HalfTimeAwayGoals is int htA)
                {
                    halfTime = new HalfTimeScore(htH, htA);
                }

                if (livePick?.LiveTime is string st)
                {
                    item.Status = st;
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
                        backgroundTipColourVIP = EvaluationHelper.EvaluateTipColor(matched?.Item, item, homeVIP, awayVIP);
                    }
                    else
                    {
                        backgroundTipColourVIP = AppColors.Black; // still pending / no numbers yet
                    }

                    if (EvaluationHelper.TryGetScores(srcHome, srcAway, out var homeVIPElo, out var awayVIPElo) && !string.IsNullOrWhiteSpace(item.VIPTipElo))
                    {
                        // pass the LIVE dto so IsFinal/clock come from live
                        backgroundTipColourVIPELO = EvaluationHelper.EvaluateTipColor(matched?.Item, item, homeVIPElo, awayVIPElo, true);
                    }
                    else
                    {
                        backgroundTipColourVIPELO = AppColors.Black; // still pending / no numbers yet
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
                //item.VIPTip = tipCode?.Code ?? item.Tip;
                item.VIPTipElo = tipCodeVIP?.Code ?? item.Tip;
                //item.Tip = "NTM";

                item.HostScore = scoreOne;
                item.GuestScore = scoreTwo;

                // NEW: apply halftime alongside other live-derived fields
                item.HalfTime = halfTime;

                item.BackgroundTipColour = backgroundTipColour;
                item.BackgroundTipColourVIP = backgroundTipColourVIP;
                item.BackgroundTipColourVIPELO = backgroundTipColourVIPELO;

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
                    $"detailsFallbackUsed={detailsFallbackUsed} detailsFallbackFailed={detailsFallbackFailed} detailsDtoEmpty={detailsDtoEmpty} " +
                    $"analyzed={analyzed} analyzeFailed={analyzeFailed} emptyProposed={emptyProposed}");

                if (debugTips && samples.Count > 0)
                {
                    Console.WriteLine($"[Tips][{dateKey}] samples:\n" +
                        JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                }
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
