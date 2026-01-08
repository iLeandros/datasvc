// Details/DetailsRefreshService.cs
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using DataSvc.Parsed;   // SnapshotPerDateStore
// using Microsoft.Extensions.Configuration; // if you end up using it in the service

namespace DataSvc.Details
{
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
}
