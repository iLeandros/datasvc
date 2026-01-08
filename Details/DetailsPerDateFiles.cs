// Details/DetailsPerDateFiles.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataSvc.Details;

public static class DetailsPerDateFiles
{
    public const string Dir = "/var/lib/datasvc/details-perdate";

    public static string PathFor(DateOnly d) => Path.Combine(Dir, $"{d:yyyy-MM-dd}.json");

    // Details/DetailsPerDateFiles.cs  (append inside the class)
    public static void CleanupRetention(DateOnly center, int back, int ahead)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in ScraperConfig.DateWindow(center, back, ahead))
                keep.Add(d.ToString("yyyy-MM-dd"));
    
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!keep.Contains(name))
                {
                    try { File.Delete(f); } catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort */ }
    }

    public static async Task SaveAsync(DateOnly d, object envelope, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(PathFor(d), json, ct);

        // optional .gz can be added later if you like
    }

    public static T? Load<T>(DateOnly d)
    {
        var p = PathFor(d);
        if (!File.Exists(p)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(p)); }
        catch { return default; }
    }

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
