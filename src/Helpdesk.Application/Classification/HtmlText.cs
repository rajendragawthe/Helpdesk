using System.Net;
using System.Text.RegularExpressions;

namespace Helpdesk.Application.Classification;

public static partial class HtmlText
{
    public static string ToPlainText(string html, int maxLength = 8000)
    {
        var withoutBlocks = ScriptOrStyleRegex().Replace(html, " ");
        var withoutTags = TagRegex().Replace(withoutBlocks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace(' ', ' ');
        var collapsed = WhitespaceRegex().Replace(decoded, " ").Trim();

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength];
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
