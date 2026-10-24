using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BidBot.Items
{
    /// <summary>
    /// Converts an HTML item table into a Discord-friendly markdown string.
    /// This is intentionally lightweight and avoids adding an HTML parser dependency.
    /// </summary>
    public class DiscordItemFactory
    {
        /// <summary>
        /// Convert the provided HTML snippet (expected to be a &lt;table class="eqitem"&gt;...&lt;/table&gt;)
        /// into a markdown string suitable for posting in Discord messages.
        /// Accepts the itemName and itemUrl; the output header will be the itemName linking to itemUrl.
        /// Any anchor tags found inside the snippet will have their URLs removed and only the link text kept.
        /// </summary>
        public string CreateFromHtml(string html, string itemName, string itemUrl)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            // Convert anchor tags to plain text: <a ...>text</a> -> text (drop href)
            var withLinks = Regex.Replace(html,
                "<a[^>]*>(?<text>.*?)</a>",
                match =>
                {
                    var text = Regex.Replace(match.Groups["text"].Value, "<.*?>", string.Empty, RegexOptions.Singleline);
                    text = WebUtility.HtmlDecode(text).Trim();
                    return EscapeMarkdown(text);
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Normalize <br> to newlines
            var withNewlines = Regex.Replace(withLinks, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);

            // Strip remaining HTML tags
            var withoutTags = Regex.Replace(withNewlines, "<.*?>", string.Empty, RegexOptions.Singleline);

            // Decode HTML entities
            var decoded = WebUtility.HtmlDecode(withoutTags).Trim();

            // Split into lines and trim
            var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            if (lines.Count == 0)
                return string.Empty;

            // Use the provided itemName as the heading. If the first line equals the itemName, skip it.
            var bodyLines = lines;
            if (!string.IsNullOrWhiteSpace(itemName) && bodyLines.Count > 0 && string.Equals(bodyLines[0], itemName, StringComparison.OrdinalIgnoreCase))
            {
                bodyLines = bodyLines.Skip(1).ToList();
            }

            var sb = new StringBuilder();


            if (bodyLines.Count > 0)
            {
                foreach (var line in bodyLines)
                {
                    // Try to format Key: Value pairs by making the key bold
                    var idx = line.IndexOf(':');
                    if (idx > 0 && idx < 60)
                    {
                        var key = line.Substring(0, idx).Trim();
                        var value = line.Substring(idx + 1).Trim();
                        sb.AppendLine($"**{EscapeMarkdown(key)}**: {EscapeMarkdown(value)}");
                    }
                    else
                    {
                        sb.AppendLine(EscapeMarkdown(line));
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            // Escape characters that Discord treats as markdown so content displays literally
            return text
                .Replace("\\", "\\\\")
                .Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("~", "\\~")
                .Replace("`", "\\`")
                .Replace("|", "\\|");
        }
    }
}
