using System;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HtmlAgilityPack;
using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.Models;

namespace DataSvc.VIPHandler;
{
    public static class VIPHelper
    {
        public static async Task<T?> GetWithRetryAsync<T>(HttpClient http, string url, JsonSerializerOptions json, CancellationToken ct)
        {
            var delayMs = 200;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
        
                    using var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if (attempt == 3) return default;
                    }
                    using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    return await JsonSerializer.DeserializeAsync<T>(stream, json, cts.Token).ConfigureAwait(false);
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs += 200; // simple backoff
                }
            }
            return default;
        }
    }
}
