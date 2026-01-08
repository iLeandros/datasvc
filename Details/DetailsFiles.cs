// Details/DetailsFiles.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
// keep this if your SaveAsync signature still shows [FromServices]
using Microsoft.AspNetCore.Mvc;

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

namespace DataSvc.Details
{
    public static class DetailsFiles
    {
        public const string File = "/var/lib/datasvc/details.json";
    
        public static async Task SaveAsync( [FromServices] DetailsStore store )
        {
            var (items, now) = store.Export();
            var json = JsonSerializer.Serialize(new { lastSavedUtc = now, items }, new JsonSerializerOptions { WriteIndented = false });
            var tmp = File + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            await System.IO.File.WriteAllTextAsync(tmp, json);
            System.IO.File.Move(tmp, File, overwrite: true);
            store.MarkSaved(now);
        }
    	/*
        public static async Task<IReadOnlyList<DetailsRecord>> LoadAsync()
        {
            if (!System.IO.File.Exists(File)) return Array.Empty<DetailsRecord>();
            try
            {
                var json = await System.IO.File.ReadAllTextAsync(File);
                var doc = JsonDocument.Parse(json);
                var items = doc.RootElement.GetProperty("items").Deserialize<List<DetailsRecord>>() ?? new();
                return items;
            }
            catch { return Array.Empty<DetailsRecord>(); }
        }
    	*/
    	public static async Task<IReadOnlyList<DetailsRecord>> LoadAsync()
    	{
    	    if (!System.IO.File.Exists(File)) return Array.Empty<DetailsRecord>();
    	    try
    	    {
    	        var json = await System.IO.File.ReadAllTextAsync(File);
    	        var doc = JsonDocument.Parse(json);
    	        var items = doc.RootElement.GetProperty("items").Deserialize<List<DetailsRecord>>() ?? new();
    	
    	        var upgraded = new List<DetailsRecord>(items.Count);
    	
    	        foreach (var rec in items)
    	        {
    	            // Use existing values if present, fall back to empty string
    	            var payload = new DetailsPayload(
    	                TeamsInfoHtml:            rec.Payload?.TeamsInfoHtml            ?? string.Empty,
    	                MatchBetweenHtml:         rec.Payload?.MatchBetweenHtml         ?? string.Empty,
    	                TeamMatchesSeparateHtml:  rec.Payload?.TeamMatchesSeparateHtml  ?? string.Empty,
    	                TeamsBetStatisticsHtml:   rec.Payload?.TeamsBetStatisticsHtml   ?? string.Empty,
    	                FactsHtml:                rec.Payload?.FactsHtml                ?? string.Empty,
    	                LastTeamsMatchesHtml:     rec.Payload?.LastTeamsMatchesHtml     ?? string.Empty,
    	                TeamsStatisticsHtml:      rec.Payload?.TeamsStatisticsHtml      ?? string.Empty,
    	                TeamStandingsHtml:        rec.Payload?.TeamStandingsHtml        ?? string.Empty
    	            );
    	
    	            // Create a new record with the upgraded payload and preserve original timestamp + href
    	            upgraded.Add(rec with { Payload = payload });
    	        }
    	
    	        return upgraded;
    	    }
    	    catch (Exception ex)
    	    {
    	        Console.WriteLine($"[details] Failed to load or migrate details.json: {ex.Message}");
    	        return Array.Empty<DetailsRecord>();
    	    }
    	}
    }
}
