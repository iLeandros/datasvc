// Parsed/BulkRefresh.cs
using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;

using DataSvc.Models;
using DataSvc.MainHelpers;

namespace DataSvc.Parsed;

public static class BulkRefresh
{
    // Admin/manual callers pass null to bypass the session freeze and force a full re-scrape.
    public static Task<(IReadOnlyList<string> Refreshed, IReadOnlyDictionary<string, string> Errors)>
        RefreshWindowAsync(
            SnapshotPerDateStore store,
            IConfiguration cfg,
            ParsedTipsService tips,
            int? hourUtc = null,
            DateOnly? center = null, int back = 3, int ahead = 3,
            CancellationToken ct = default)
        => RefreshWindowAsync(store, cfg, tips, donePastDates: null, hourUtc, center, back, ahead, ct);

    public static async Task<(IReadOnlyList<string> Refreshed, IReadOnlyDictionary<string, string> Errors)>
        RefreshWindowAsync(
            SnapshotPerDateStore store,
            IConfiguration cfg,
            ParsedTipsService tips,
            HashSet<DateOnly>? donePastDates,
            int? hourUtc = null,
            DateOnly? center = null, int back = 3, int ahead = 3,
            CancellationToken ct = default)
    {
        var c = center ?? ScraperConfig.TodayLocal();
        var dates = ScraperConfig.DateWindow(c, back, ahead).ToArray();

        var refreshed = new List<string>(dates.Length);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in dates)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                if (d < c)
                {
                    // Past dates are processed exactly ONCE per session.
                    // A date is only added to donePastDates after it has match items,
                    // so empty scrapes don't permanently freeze the date.
                    if (donePastDates != null && donePastDates.Contains(d))
                    {
                        refreshed.Add(d.ToString("yyyy-MM-dd"));
                        continue;
                    }

                    // Try in-memory first, then disk.
                    if (!store.TryGet(d, out var existing) || !HasMatchData(existing))
                    {
                        if (TryLoadFromDisk(store, d))
                            store.TryGet(d, out existing);
                    }

                    if (HasMatchData(existing))
                    {
                        // If the snapshot already has tips applied (VIPTip set on all items),
                        // freeze it as-is — avoids re-analysis with a potentially incomplete
                        // DetailsStore after restart.
                        // If tips are missing, apply them once now, then save and freeze.
                        if (!HasTipsApplied(existing) &&
                            existing!.Payload?.TableDataGroup is { } g && g.Count > 0)
                        {
                            await tips.ApplyTipsForDate(d, g, ct);
                            await SaveSnapshotAsync(existing, d, ct);
                        }

                        donePastDates?.Add(d);
                        refreshed.Add(d.ToString("yyyy-MM-dd"));
                        continue;
                    }

                    // No data in memory or on disk — fall through to scrape once.
                }

                var snap = await ScraperService.FetchOneDateAsync(d, cfg, hourUtc, ct);

                if (snap.Payload?.TableDataGroup is { } groups && groups.Count > 0)
                {
                    await tips.ApplyTipsForDate(d, groups, ct);

                    if (d < c)
                    {
                        await SaveSnapshotAsync(snap, d, ct);
                        donePastDates?.Add(d);
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

    // True when every item that can carry a tip already has VIPTip set.
    // VIPTip is only non-null after ApplyTipsForDate has processed the item
    // (it falls back to item.Tip, so it's never null post-application).
    private static bool HasTipsApplied(DataSnapshot? snap) =>
        snap?.Payload?.TableDataGroup is { Count: > 0 } groups &&
        groups.SelectMany(g => g?.Items ?? Enumerable.Empty<TableDataItem>())
              .All(item => item.VIPTip != null);

    private static bool HasMatchData(DataSnapshot? snap) =>
        snap?.Payload?.TableDataGroup is { Count: > 0 } groups &&
        groups.Any(g => g?.Items?.Count > 0);

    private static async Task SaveSnapshotAsync(DataSnapshot snap, DateOnly date, CancellationToken ct)
    {
        var path = ScraperConfig.SnapshotPath(date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false }), ct);
    }

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
                try { File.Delete(file); } catch { }
            }
        }
    }

    public static bool TryLoadFromDisk(SnapshotPerDateStore store, DateOnly date)
    {
        var path = ScraperConfig.SnapshotPath(date);
        if (!File.Exists(path)) return false;
        var json = File.ReadAllText(path);
        var snap = JsonSerializer.Deserialize<DataSnapshot>(json);
        if (snap is null) return false;
        store.Set(date, snap);
        return true;
    }
}
