// Details/DetailsStore.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

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
    public sealed class DetailsStore
    {
        private readonly ConcurrentDictionary<string, DetailsRecord> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _saveGate = new();
        public DateTimeOffset? LastSavedUtc { get; private set; }
    
        public void Set(DetailsRecord rec)
    	{
    	    var norm = Normalize(rec.Href);
    	    if (!string.Equals(rec.Href, norm, StringComparison.OrdinalIgnoreCase))
    	        rec = rec with { Href = norm };
    	
    	    _map[norm] = rec;
    	}
        public DetailsRecord? Get(string href) => _map.TryGetValue(Normalize(href), out var rec) ? rec : null;
    
        public List<(string href, DateTimeOffset lastUpdatedUtc)> Index()
            => _map.Values.Select(v => (v.Href, v.LastUpdatedUtc)).ToList();
    
        public (IReadOnlyList<DetailsRecord> items, DateTimeOffset now) Export()
            => (_map.Values.ToList(), DateTimeOffset.UtcNow);
    
        public void Import(IEnumerable<DetailsRecord> items)
        {
            _map.Clear();
            foreach (var it in items) _map[it.Href] = it;
        }
    	/// <summary>
        /// Remove any cached href that is NOT present in <paramref name="keep"/>.
        /// Returns the number of removed items.
        /// </summary>
        public int ShrinkTo(IReadOnlyCollection<string> keep)
        {
            var set = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var key in _map.Keys)
            {
                if (!set.Contains(key))
                {
                    if (_map.TryRemove(key, out _)) removed++;
                }
            }
            return removed;
        }
        public void MarkSaved(DateTimeOffset ts) { lock (_saveGate) LastSavedUtc = ts; }
    
       public static string Normalize(string href)
    	{
    	    if (string.IsNullOrWhiteSpace(href)) return "";
    	
    	    var s = WebUtility.HtmlDecode(href).Trim();
    	
    	    // If it's already absolute, return the canonical AbsoluteUri.
    	    // This handles inputs like ".../Slough Town (England)/..." by encoding to %20.
    	    if (Uri.TryCreate(s, UriKind.Absolute, out var abs))
    	        return abs.AbsoluteUri;
    	
    	    // Protocol-relative (//host/...)
    	    if (s.StartsWith("//")) return "https:" + s;
    	
    	    // Host without scheme
    	    if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
    	        s.StartsWith("statarea.com", StringComparison.OrdinalIgnoreCase))
    	        return "https://" + s.TrimStart('/');
    			//return "http://" + s.TrimStart('/');
    	
    	    // Site-relative
    	    var baseUri = new Uri("https://www.statarea.com/");
    	    //var baseUri = new Uri("http://www.statarea.com/");
    	    if (!s.StartsWith("/")) s = "/" + s;
    	    return new Uri(baseUri, s).AbsoluteUri; // canonicalize
    	}
    }
}
