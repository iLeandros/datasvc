using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;


namespace DataSvc.Likes;

public static class VoteTotalsRepo
{
    private static byte[] Sha256(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s ?? string.Empty));

    // Returns href -> score for all hrefs that exist in SQL (others omitted)
    public static async Task<Dictionary<string,int>> GetScoresByHrefAsync(
        MySqlConnection conn, IEnumerable<string> hrefs, CancellationToken ct = default)
    {
        var list = hrefs.Where(h => !string.IsNullOrWhiteSpace(h))
                        .Select(h => (Href: h.Trim(), Hash: Sha256(h.Trim())))
                        .Distinct()
                        .ToArray();

        if (list.Length == 0) return new();

        var rows = await conn.QueryAsync<(byte[] Hash, int Score)>(@"
            SELECT m.href_hash AS Hash, t.score AS Score
              FROM matches m
              JOIN match_vote_totals t ON t.match_id = m.match_id
             WHERE m.href_hash IN @hashes;",
            new { hashes = list.Select(x => x.Hash).ToArray() });

        // map back to href using the input list
        var byHash = list.ToDictionary(x => Convert.ToHexString(x.Hash), x => x.Href, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = Convert.ToHexString(r.Hash);
            if (byHash.TryGetValue(key, out var href))
                result[href] = r.Score;
        }
        return result;
    }
}
