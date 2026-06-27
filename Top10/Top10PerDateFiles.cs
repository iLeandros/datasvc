// Top10/Top10PerDateFiles.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

namespace DataSvc.Top10;

public static class Top10PerDateFiles
{
    public const string Dir = "/var/lib/datasvc/top10";
    public static string PathFor(DateOnly d) => Path.Combine(Dir, $"{d:yyyy-MM-dd}.json");

    public static async Task SaveAsync(DateOnly d, DataSnapshot snap, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(PathFor(d), json, ct);
    }

    public static DataSnapshot? Load(DateOnly d)
    {
        var p = PathFor(d);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<DataSnapshot>(File.ReadAllText(p)); }
        catch { return null; }
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
