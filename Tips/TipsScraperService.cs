// Tips/TipsScraperService.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataSvc.Details; // for DetailsStore.Normalize used by your table parser

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

namespace DataSvc.Tips;

public sealed class TipsScraperService
{
    private readonly TipsStore _store;
    public TipsScraperService(TipsStore store) => _store = store;

    public async Task<DataSnapshot> FetchAndStoreAsync(CancellationToken ct = default)
    {
        try
        {
            var center = ScraperConfig.TodayLocal();
            var dates = ScraperConfig.DateWindow(center, 3, 0); // back:3, ahead:0

            DataSnapshot? lastToday = null;

            foreach (var d in dates)
            {
                ct.ThrowIfCancellationRequested();

                var iso = d.ToString("yyyy-MM-dd");
                var url = $"https://www.statarea.com/tips/date/{iso}/";

                var html   = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
                var titles = GetStartupMainTitlesAndHrefs2024.GetStartupMainTitlesAndHrefs(html);
                var table  = GetStartupMainTableDataGroup2024.GetStartupMainTableDataGroup(html);

                var payload = new DataPayload(html, titles, table);
                var snap = new DataSnapshot(DateTimeOffset.UtcNow, true, payload, null);

                await TipsPerDateFiles.SaveAsync(d, snap, ct);
                if (d == center) lastToday = snap;
            }

            // prune: keep only D-3..today
            var keep = dates.Select(d => d.ToString("yyyy-MM-dd")).ToHashSet(StringComparer.Ordinal);
            PruneOldFiles(TipsPerDateFiles.Dir, keep);

            if (lastToday is not null)
            {
                _store.Set(lastToday);
                await TipsFiles.SaveAsync(lastToday);
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
