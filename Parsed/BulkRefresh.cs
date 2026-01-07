namespace DataSvc.Parsed;

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
