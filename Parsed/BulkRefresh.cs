// Parsed/BulkRefresh.cs
using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth;
using DataSvc.MainHelpers;
using DataSvc.Likes;
using DataSvc.Services;
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;


namespace DataSvc.Parsed;

public static class BulkRefresh
{
    public static async Task<(IReadOnlyList<string> Refreshed, IReadOnlyDictionary<string,string> Errors)>
    	RefreshWindowAsync(
        SnapshotPerDateStore store,
        IConfiguration cfg,
		    ParsedTipsService tips,
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

                // Past dates: skip only when we already have a snapshot with actual match
                // items (all key fields filled). Re-scraping risks overwriting good historical
                // data with empty results (the source may no longer serve past-day data).
                // Check memory first, then fall back to disk.
                if (d < c)
                {
                    if (store.TryGet(d, out var mem) && HasMatchData(mem))
                    {
                        refreshed.Add(d.ToString("yyyy-MM-dd"));
                        continue;
                    }
                    // Try to restore from disk; if that snapshot also has data we can skip.
                    if (TryLoadFromDisk(store, d) && store.TryGet(d, out var fromDisk) && HasMatchData(fromDisk))
                    {
                        refreshed.Add(d.ToString("yyyy-MM-dd"));
                        continue;
                    }
                    // No usable data found — fall through and scrape once.
                }

                var snap = await ScraperService.FetchOneDateAsync(d, cfg, hourUtc, ct);

                if (snap.Payload?.TableDataGroup is { } groups && groups.Count > 0)
                {
                    await tips.ApplyTipsForDate(d, groups, ct);

                    // For past dates, overwrite the disk file after tips are applied so that
                    // future service restarts load fully-completed snapshots and can skip immediately.
                    if (d < c)
                    {
                        var path = ScraperConfig.SnapshotPath(d);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllTextAsync(path,
                            JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false }), ct);
                    }
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

    // Returns true when the snapshot contains at least one group with match items.
    // An empty snapshot (no items) does not qualify — we should try to (re-)scrape.
    private static bool HasMatchData(DataSnapshot? snap) =>
        snap?.Payload?.TableDataGroup is { Count: > 0 } groups &&
        groups.Any(g => g?.Items?.Count > 0);

	public static void CleanupRetention(SnapshotPerDateStore store, DateOnly center, int back, int ahead)
    {
        var keep = new HashSet<DateOnly>(ScraperConfig.DateWindow(center, back, ahead));
        store.PruneTo(keep);

        var dir = Path.GetDirectoryName(ScraperConfig.SnapshotPath(center))!;
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
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
