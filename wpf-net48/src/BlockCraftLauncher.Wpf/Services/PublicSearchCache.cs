using System;
using System.Collections.Concurrent;
using System.Linq;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Small in-memory cache for PUBLIC, unauthenticated resource searches only.
    /// Never persist searches, tokens or responses to disk. Do not cache
    /// Microsoft/Mojang account endpoints or arbitrary third-party URLs.
    /// Cache raw JSON, not mutable result objects, so each caller parses anew.
    /// </summary>
    internal static class PublicSearchCache
    {
        sealed class Entry
        {
            internal string Body;
            internal DateTime ExpiresUtc;
        }

        const int MaxEntries = 96;
        const int MaxCachedCharacters = 512 * 1024;
        internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
        static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        internal static string CacheKey(string url, string mode, string proxy, bool mcim)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length > 0)
                return null;

            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath;
            bool allowed =
                (host == "api.modrinth.com" && path.StartsWith("/v2/", StringComparison.Ordinal)) ||
                (host == "mod.mcimirror.top" && mcim &&
                    path.StartsWith("/curseforge/v1/", StringComparison.Ordinal)) ||
                (host == "mosaicary.com" &&
                    (path == "/api/skins" || path.StartsWith("/api/skins/", StringComparison.Ordinal))) ||
                (host == "dpplugins.com" && path == "/api/skins") ||
                (host == "api.github.com" &&
                    (path == "/search/repositories" ||
                     System.Text.RegularExpressions.Regex.IsMatch(path,
                        @"^/repos/[A-Za-z0-9-]{1,39}/[A-Za-z0-9_.-]{1,100}(?:/releases/latest)?$")));
            if (!allowed) return null;

            // A proxy or network-mode change must not reuse a response produced
            // under different route preferences. No authentication is involved.
            return (mode ?? "") + "\n" + (proxy ?? "") + "\n" +
                (mcim ? "1" : "0") + "\n" + uri.AbsoluteUri;
        }

        internal static bool TryGet(string key, out string body)
        {
            body = null;
            if (key == null || !Entries.TryGetValue(key, out var entry))
                return false;
            if (DateTime.UtcNow >= entry.ExpiresUtc)
            {
                Entries.TryRemove(key, out _);
                return false;
            }
            body = entry.Body;
            return true;
        }

        internal static void Remove(string key)
        {
            if (key != null) Entries.TryRemove(key, out _);
        }

        internal static void Store(string key, string body, TimeSpan? lifetime = null)
        {
            if (key == null || string.IsNullOrWhiteSpace(body) ||
                body.Length > MaxCachedCharacters) return;

            var now = DateTime.UtcNow;
            if (Entries.Count >= MaxEntries)
            {
                foreach (var item in Entries.Where(x => x.Value.ExpiresUtc <= now))
                    Entries.TryRemove(item.Key, out _);
                if (Entries.Count >= MaxEntries)
                {
                    // Bounded total memory even for thousands of unique search
                    // terms. Oldest 1/4 is evicted, not all warm results.
                    foreach (var item in Entries.OrderBy(x => x.Value.ExpiresUtc)
                        .Take(MaxEntries / 4))
                        Entries.TryRemove(item.Key, out _);
                }
            }
            Entries[key] = new Entry {
                Body = body,
                ExpiresUtc = now.Add(lifetime ?? Ttl)
            };
        }
    }
}
