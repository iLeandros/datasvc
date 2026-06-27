// LiveScores/LiveScoresScraperService.cs
using System;
using System.Linq;
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

namespace DataSvc.LiveScores;

public sealed class LiveScoresScraperService
{
    private readonly LiveScoresStore _store;
    private readonly TimeZoneInfo _tz;

    public LiveScoresScraperService(LiveScoresStore store)
    {
        _store = store;
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    static string BuildUrl(DateTime localDay)
    {
        var iso = localDay.ToString("yyyy-MM-dd");
        return $"https://www.statarea.com/livescore/date/{iso}/";
    }

    public async Task<(int Refreshed, DateTimeOffset LastUpdatedUtc)> FetchAndStoreAsync(CancellationToken ct = default)
    {
        int refreshed = 0;

        var center = ScraperConfig.TodayLocal();
        var dates = ScraperConfig.DateWindow(center, back: 3, ahead: 3).ToList();

        foreach (var dateOnly in dates)
        {
            ct.ThrowIfCancellationRequested();

            var localDay = new DateTime(dateOnly.Year, dateOnly.Month, dateOnly.Day);
            var url = BuildUrl(localDay);

            string html;
            try
            {
                html = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo(url);
            }
            catch
            {
                if (dateOnly == center)
                    html = await GetStartupMainPageFullInfo2024.GetStartupMainPageFullInfo("https://www.statarea.com/livescore");
                else
                    throw;
            }

            var dateIso = dateOnly.ToString("yyyy-MM-dd");
            var day = await LiveScoresParser.ParseDay(html, dateIso);
            _store.Set(day);
            refreshed++;
        }

        var keep = dates.Select(d => d.ToString("yyyy-MM-dd")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _store.ShrinkTo(keep);

        await LiveScoresFiles.SaveAsync(_store);
        return (refreshed, DateTimeOffset.UtcNow);
    }
}
