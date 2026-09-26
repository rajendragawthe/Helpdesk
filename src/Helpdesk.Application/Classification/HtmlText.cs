using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Helpdesk.Application.Classification;

public static partial class HtmlText
{
    // Hostile emails can be arbitrarily large; bound the work before any scanning.
    private const int MaxRawLength = 64_000;

    public static string ToPlainText(string html, int maxLength = 8000)
    {
        if (html.Length > MaxRawLength)
        {
            html = html[..MaxRawLength];
        }

        var withoutBlocks = RemoveBlocks(RemoveBlocks(html, "script"), "style");
        var withoutTags = RemoveTags(withoutBlocks);
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace(' ', ' ');
        var collapsed = WhitespaceRegex().Replace(decoded, " ").Trim();

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength];
    }

    // Removes <name ...> ... </name> blocks. Linear-time (no regex backtracking): once a block has no
    // closing tag, no later block can have one either, so the scan stops instead of rescanning.
    private static string RemoveBlocks(string html, string name)
    {
        var open = "<" + name;
        var close = "</" + name;
        var sb = new StringBuilder(html.Length);
        var pos = 0;

        while (pos < html.Length)
        {
            var start = html.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var afterName = start + open.Length;
            var isBlockStart = afterName >= html.Length || !char.IsLetterOrDigit(html[afterName]) && html[afterName] != '_';
            if (!isBlockStart)
            {
                sb.Append(html, pos, afterName - pos);
                pos = afterName;
                continue;
            }

            var closeStart = html.IndexOf(close, afterName, StringComparison.OrdinalIgnoreCase);
            var closeEnd = closeStart < 0 ? -1 : html.IndexOf('>', closeStart);
            if (closeEnd < 0)
            {
                break;
            }

            sb.Append(html, pos, start - pos).Append(' ');
            pos = closeEnd + 1;
        }

        sb.Append(html, pos, html.Length - pos);
        return sb.ToString();
    }

    // Replaces <...> tags with a space. Linear-time: when no '>' remains, the rest is plain text.
    private static string RemoveTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        var pos = 0;

        while (pos < html.Length)
        {
            var lt = html.IndexOf('<', pos);
            if (lt < 0)
            {
                break;
            }

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                break;
            }

            if (gt == lt + 1)
            {
                // "<>" is not a tag; keep the '<' as text.
                sb.Append(html, pos, gt - pos);
                pos = gt;
                continue;
            }

            sb.Append(html, pos, lt - pos).Append(' ');
            pos = gt + 1;
        }

        sb.Append(html, pos, html.Length - pos);
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRegex();
}
