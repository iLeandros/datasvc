// LiveScores/Files.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
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

namespace DataSvc.LiveScores;

public static class LiveScoresFiles
{
    public const string File = "/var/lib/datasvc/livescores.json";

    public static async Task SaveAsync(LiveScoresStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var (items, now) = store.Export();
        var envelope = new { lastSavedUtc = now, days = items };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = false });
        var tmp = File + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, File, overwrite: true);

        try
        {
            var gzPath = File + ".gz";
            await using var input = System.IO.File.OpenRead(File);
            await using var output = System.IO.File.Create(gzPath);
            using var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: false);
            await input.CopyToAsync(gz);
        }
        catch { /* non-fatal */ }

        store.MarkSaved(now);
    }

    public static async Task<(List<LiveScoreDay> days, DateTimeOffset? lastSavedUtc)?> LoadAsync()
    {
        if (!System.IO.File.Exists(File)) return null;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(File);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var last = root.TryGetProperty("lastSavedUtc", out var tsEl) && tsEl.ValueKind is not JsonValueKind.Null
                ? tsEl.GetDateTimeOffset()
                : (DateTimeOffset?)null;

            var days = new List<LiveScoreDay>();
            if (root.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in daysEl.EnumerateArray())
                {
                    var date = d.GetProperty("Date").GetString() ?? "";
                    var groups = new List<LiveScoreGroup>();
                    if (d.TryGetProperty("Groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in groupsEl.EnumerateArray())
                        {
                            var comp = g.GetProperty("Competition").GetString() ?? "";
                            var matches = new List<LiveScoreItem>();
                            if (g.TryGetProperty("Matches", out var msEl) && msEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var m in msEl.EnumerateArray())
                                {
                                    var actions = new List<MatchAction>();
                                    if (m.TryGetProperty("Action", out var actEl) && actEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var a in actEl.EnumerateArray())
                                        {
                                            var side = (TeamSide)a.GetProperty("Side").GetInt32();
                                            var kind = (ActionKind)a.GetProperty("Kind").GetInt32();
                                            int? minute = a.TryGetProperty("Minute", out var minEl) && minEl.ValueKind != JsonValueKind.Null
                                                ? minEl.GetInt32()
                                                : (int?)null;
                                            var player = a.GetProperty("Player").GetString() ?? "";
                                            actions.Add(new MatchAction(side, kind, minute, player));
                                        }
                                    }

                                    matches.Add(new LiveScoreItem(
                                        m.GetProperty("Time").GetString() ?? "",
                                        m.GetProperty("Status").GetString() ?? "",
                                        m.GetProperty("HomeTeam").GetString() ?? "",
                                        m.GetProperty("AwayTeam").GetString() ?? "",
                                        m.GetProperty("HomeScore").GetInt32(),
                                        m.GetProperty("AwayScore").GetInt32(),
                                        m.GetProperty("Href").GetString() ?? "",
                                        actions
                                    ));
                                }
                            }
                            groups.Add(new LiveScoreGroup(comp, matches));
                        }
                    }
                    days.Add(new LiveScoreDay(date, groups));
                }
            }

            return (days, last);
        }
        catch { return null; }
    }
}
