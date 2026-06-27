// Infrastructure/UserIdentity.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace DataSvc.Infrastructure;

/// <summary>
/// Centralises user-ID extraction so every controller uses the same logic
/// instead of copy-pasting it.  Reads (in order):
///   1. HttpContext.Items["userId"]  — set by SessionAuthHandler
///   2. ClaimTypes.NameIdentifier / "uid" / "sub" claims
/// </summary>
public static class UserIdentity
{
    public static bool TryGetUserId(HttpContext ctx, out ulong userId)
    {
        userId = 0;

        // 1) SessionAuthHandler stores the value here (key matches what the handler sets)
        if (ctx.Items.TryGetValue("userId", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out userId))
            return true;

        // 2) Fallback to common claim types
        string?[] candidates =
        {
            ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier),
            ctx.User?.FindFirstValue("uid"),
            ctx.User?.FindFirstValue("sub"),
        };

        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && ulong.TryParse(c, out userId))
                return true;

        return false;
    }
}
