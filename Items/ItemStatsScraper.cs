using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BidBot.Items
{
    public sealed class ItemStats
    {
        // RawHtml contains the original table snippet
        public string RawHtml { get; init; } = string.Empty;
        // RawText is the table converted to plain text (no header/url content)
        public string RawText { get; init; } = string.Empty;
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ItemStatsScraper
    {
        // Use an HttpClient backed by a CookieContainer and automatic decompression so we can follow
        // any server-set cookies and handle compressed responses. Some pages issue a meta-refresh
        // to set a cookie before returning the real content; this client + helper method below
        // will follow such meta-refresh targets.
        private static readonly HttpClient httpClient = CreateHttpClient();
        private readonly ILogger<ItemStatsScraper> logger;
        private readonly ItemListStore itemListStore;
        private readonly BidbotDataStore store;

        public ItemStatsScraper(ILogger<ItemStatsScraper> logger, ItemListStore itemListStore, BidbotDataStore store)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.itemListStore = itemListStore ?? throw new ArgumentNullException(nameof(itemListStore));
            this.store = store;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer()
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BidBot/1.0 (+https://example.com)");
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            }
            catch { }

            return client;
        }

        /// <summary>
        /// Find the item by id in the ItemListStore, check the local ItemHtmlStore JSON file for cached HTML,
        /// otherwise fetch the item's page, extract the &lt;table class="eqitem"&gt;...&lt;/table&gt; HTML, persist it and return it.
        /// Returns the raw HTML snippet or null when not found.
        /// </summary>
        public async Task<string?> GetTableHtmlById(int id, CancellationToken ct = default)
        {
            // Check persisted JSON store first
            store.ItemHtmlEntries.TryGetValue(id, out var persisted);
            if (persisted != null && !string.IsNullOrWhiteSpace(persisted.Html))
                return persisted.Html;

            var list = itemListStore.Get();
            if (!list.TryGetById(id, out var entry) || entry == null)
            {
                logger.LogDebug("Item id {Id} not found in item list.", id);
                return null;
            }

            var url = entry.LucyLink ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                logger.LogDebug("Item id {Id} has no lucylink.", id);
                return null;
            }

            // If the link is relative, prefix with the default host
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (url.StartsWith("/"))
                    url = "https://allakhazam.com" + url;
                else
                    url = "https://allakhazam.com/" + url;
            }

            string html;
            try
            {
                html = await FetchStringFollowingMetaRefreshAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch url {Url} for item {Id}", url, id);
                return null;
            }

            var snippet = ExtractEqItemTable(html);
            if (snippet == null)
            {
                logger.LogDebug("No eqitem table found at {Url} for item {Id}", url, id);
                return null;
            }

            // Persist to JSON store for future use
            try
            {
                var newEntry = new ItemHtmlEntry { ID = id, Name = entry.Name, Html = snippet, ScrapedAt = DateTime.UtcNow };
                store.ItemHtmlEntries[id] = newEntry;
                store.SaveChanges();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist item html for {Id}", id);
            }

            return snippet;
        }

        /// <summary>
        /// Fetch the item table HTML (uses the existing GetTableHtmlById which consults the cache)
        /// then convert it into a lightweight ItemStats object containing text lines and key/value fields.
        /// This method intentionally does not add any header or url information — it only returns the
        /// parsed stats. Header and url presentation will be handled elsewhere.
        /// </summary>
        public async Task<ItemStats?> GetStatsById(int id, CancellationToken ct = default)
        {
            var snippet = await GetTableHtmlById(id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(snippet))
                return null;

            // The new item window puts the item name in a figcaption. It is presentation
            // metadata rather than an item stat, so remove it before extracting lines.
            var withoutCaption = Regex.Replace(snippet,
                "<figcaption\\b[^>]*>.*?</figcaption>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Convert anchors to their inner text (drop href)
            var withLinks = Regex.Replace(withoutCaption,
                "<a[^>]*>(?<text>.*?)</a>",
                match =>
                {
                    var text = Regex.Replace(match.Groups["text"].Value, "<.*?>", string.Empty, RegexOptions.Singleline);
                    text = WebUtility.HtmlDecode(text).Trim();
                    return text;
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Normalize line and block elements to newlines. The current item markup uses one <p>
            // element per stat line rather than <br> elements.
            var withNewlines = Regex.Replace(withLinks, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            withNewlines = Regex.Replace(withNewlines,
                "</?(?:p|div|figcaption|tr|li|td|th)\\b[^>]*>",
                "\n",
                RegexOptions.IgnoreCase);

            // Strip remaining HTML tags
            var withoutTags = Regex.Replace(withNewlines, "<.*?>", string.Empty, RegexOptions.Singleline);

            // Decode HTML entities and trim
            var decoded = WebUtility.HtmlDecode(withoutTags).Trim();

            // Split into non-empty lines
            var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // Heuristic: if the first line looks like a header (no colon) and the following line looks like a Key: Value pair,
            // drop the first line. The caller will construct presentation (name/url) elsewhere.
            if (lines.Count > 1 && !lines[0].Contains(":") && lines[1].Contains(":"))
            {
                lines = lines.Skip(1).ToList();
            }

            // Remove any lone URL lines
            lines = lines.Where(l => !(l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))).ToList();

            // Build fields from lines that look like "Key: Value"
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx > 0 && idx < 120)
                {
                    var key = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        // If key already exists, append with a separator
                        if (dict.TryGetValue(key, out var existing))
                            dict[key] = existing + "; " + value;
                        else
                            dict[key] = value;
                    }
                }
            }

            var stats = new ItemStats
            {
                RawHtml = snippet,
                RawText = string.Join('\n', lines),
                Lines = lines,
                Fields = dict
            };

            return stats;
        }

        private static string? ExtractEqItemTable(string html)
        {
            if (string.IsNullOrEmpty(html))
                return null;

            var ci = StringComparison.OrdinalIgnoreCase;

            // Find the legacy class="eqitem" table.
            var classIdx = html.IndexOf("class=\"eqitem\"", ci);
            if (classIdx < 0)
                classIdx = html.IndexOf("class='eqitem'", ci);
            if (classIdx < 0)
                classIdx = html.IndexOf("eqitem", ci);

            if (classIdx >= 0)
            {
                // find the opening <table before the class occurrence
                var tableOpen = html.LastIndexOf("<table", classIdx, ci);
                if (tableOpen < 0)
                    tableOpen = html.IndexOf("<table", ci);
                if (tableOpen < 0)
                    return null;

                var tableClose = html.IndexOf("</table>", classIdx, ci);
                if (tableClose < 0)
                    return null;

                var end = tableClose + "</table>".Length;
                return html.Substring(tableOpen, end - tableOpen);
            }

            // The current site renders the item window as a <figure> with a caption
            // and one <p> element for each line of item text.
            var figureMatches = Regex.Matches(html,
                "<figure\\b[^>]*\\baria-label\\s*=\\s*[\"'][^\"']*[\"'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match figureMatch in figureMatches)
            {
                var figureClose = html.IndexOf("</figure>", figureMatch.Index + figureMatch.Length, ci);
                if (figureClose < 0)
                    continue;

                var figureEnd = figureClose + "</figure>".Length;
                var figure = html.Substring(figureMatch.Index, figureEnd - figureMatch.Index);
                if (figure.IndexOf("<figcaption", ci) >= 0 && figure.IndexOf("<p", ci) >= 0)
                    return figure;
            }

            return null;
        }

        private static async Task<string> FetchStringFollowingMetaRefreshAsync(string url, CancellationToken ct)
        {
            const int maxHops = 4;
            var current = url;

            for (int hop = 0; hop < maxHops; hop++)
            {
                using var resp = await httpClient.GetAsync(current, ct).ConfigureAwait(false);
                var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Look for meta refresh: <meta http-equiv="refresh" content="0; URL=/item.html?id=25210&setcookie=1">
                var metaMatch = Regex.Match(content,
                    "<meta[^>]+http-equiv\\s*=\\s*['\"]?refresh['\"]?[^>]*content\\s*=\\s*['\"]?\\s*\\d+\\s*;\\s*url\\s*=\\s*(.*?)['\"\\s>]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (!metaMatch.Success)
                {
                    // No meta refresh found — return this content
                    return content;
                }

                // Extract and normalize the URL from the meta tag
                var raw = WebUtility.HtmlDecode(metaMatch.Groups[1].Value).Trim('"', '\'', ' ', '\t', '\r', '\n');
                if (string.IsNullOrEmpty(raw))
                    return content;

                // Resolve relative URLs against current
                try
                {
                    var baseUri = new Uri(current);
                    var nextUri = new Uri(baseUri, raw);
                    current = nextUri.ToString();
                    // Loop and fetch the next url (cookies persist because of the CookieContainer)
                    continue;
                }
                catch
                {
                    // If URI resolving fails, return what we have
                    return content;
                }
            }

            // If we hit hop limit, do a final fetch of the last url
            using var finalResp = await httpClient.GetAsync(current, ct).ConfigureAwait(false);
            return await finalResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }
}
