using DataSvc.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DataSvc.Analyzer;

public sealed class ClubEloService : IClubEloService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ClubEloService(HttpClient http) => _http = http;

    public async Task<ClubEloResponse> GetCurrentAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "data/clubelo/current");
            //req.Headers.UserAgent.ParseAdd("YourApp/1.0 (+maui)");
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<ClubEloResponse>(stream, _json, ct)
                   ?? new ClubEloResponse();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in IClubService GetCurrentAsync : {ex.Message}"); 
            return new ClubEloResponse();
        }
    }
}
