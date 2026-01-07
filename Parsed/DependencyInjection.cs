// Parsed/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataSvc.Parsed
{
    public static class DependencyInjection
    {
        /// <summary>
        /// Registers the Parsed module services and jobs.
        /// </summary>
        public static IServiceCollection AddParsedServices(this IServiceCollection services)
        {
            // one-snapshot-per-date store
            services.AddSingleton<SnapshotPerDateStore>();       // already in Program.cs
            // tips/enrichment service used during refresh
            services.AddSingleton<ParsedTipsService>();          // already in Program.cs
            // background job that refreshes the D±3 window and enforces retention
            services.AddHostedService<PerDateRefreshJob>();      // already in Program.cs

            // Optional: enable if you want a periodic tips re-apply pass over in-memory snapshots
            services.AddHostedService<ParsedTipsRefreshJob>();

            return services;
        }

        /// <summary>
        /// Loads existing snapshot files from disk into memory for today±3.
        /// Call right after app.Build().
        /// </summary>
        public static void WarmParsedSnapshotsFromDisk(this IServiceProvider services)
        {
            var store = services.GetRequiredService<SnapshotPerDateStore>();
            var center = ScraperConfig.TodayLocal();
            foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                BulkRefresh.TryLoadFromDisk(store, d);
        }
    }
}
