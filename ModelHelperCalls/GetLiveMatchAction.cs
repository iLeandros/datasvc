using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using DataSvc.Models; // BarChart, MatchFactData

public class GetLiveMatchActionHelper
{
    private readonly HttpClient _httpClient;

    public GetLiveMatchActionHelper(HttpClient httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string> GetLivescoreMatchActionsAsync(string matchId, string dateIso)
    {
        // This matches: --data-urlencode 'object={"action":"getLivescoreMatchActions","matchid":"1455341"}'
        var payload = new
        {
            action = "getLivescoreMatchActions",
            matchid = matchId
        };

        string jsonObject = JsonSerializer.Serialize(payload);

        var formContent = new FormUrlEncodedContent(new[]
        {
        new KeyValuePair<string, string>("object", jsonObject)
    });

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.statarea.com/actions/controller/"
        )
        {
            Content = formContent
        };

        // Headers from your curl
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Origin", "https://www.statarea.com");

        // Use today's date in the referer URL (or hard-code if you prefer)
        var refererDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        request.Headers.Referrer = new Uri($"https://www.statarea.com/livescore/date/{refererDate}/");

        // User-Agent
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/142.0.0.0 Safari/537.36"
        );

        // Content-Type will be set automatically to application/x-www-form-urlencoded; charset=utf-8

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }
}
