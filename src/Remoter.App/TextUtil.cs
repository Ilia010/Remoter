using System.Text;
using System.Text.RegularExpressions;

namespace Remoter.App;

public static partial class TextUtil
{
    public static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Готовый HTML → обычный текст. Нужен как запасной вариант, если Telegram не разобрал разметку.</summary>
    public static string HtmlToPlain(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var text = TagRegex().Replace(html, "");
        return text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&amp;", "&");
    }

    /// <summary>
    /// Режет готовый HTML по строкам, не разрывая теги. Если встретилась строка длиннее лимита,
    /// возвращает null — вызывающий сам решит, отправить текстом или файлом.
    /// </summary>
    public static IReadOnlyList<string>? SplitByLines(string text, int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 200, 4096);
        if (string.IsNullOrEmpty(text)) return [""];
        if (text.Length <= maxChars) return [text];

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length > maxChars) return null;

            if (current.Length + line.Length + 1 > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0) chunks.Add(current.ToString().TrimEnd('\n'));
        return chunks.Count == 0 ? [""] : chunks;
    }

    /// <summary>Мини-Markdown → HTML, который понимает Telegram.</summary>
    public static string ToTelegramHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var sb = new StringBuilder(markdown.Length + 64);
        var index = 0;

        while (index < markdown.Length)
        {
            var fence = markdown.IndexOf("```", index, StringComparison.Ordinal);
            if (fence < 0)
            {
                sb.Append(Inline(markdown[index..]));
                break;
            }

            sb.Append(Inline(markdown[index..fence]));

            var end = markdown.IndexOf("```", fence + 3, StringComparison.Ordinal);
            var block = end < 0 ? markdown[(fence + 3)..] : markdown[(fence + 3)..end];

            var newline = block.IndexOf('\n');
            if (newline > 0)
            {
                var first = block[..newline].Trim();
                if (first.Length is > 0 and < 24 && !first.Contains(' '))
                    block = block[(newline + 1)..];
            }

            sb.Append("<pre><code>")
              .Append(EscapeHtml(block.TrimEnd('\n')))
              .Append("</code></pre>");

            index = end < 0 ? markdown.Length : end + 3;
        }

        return sb.ToString();
    }

    private static string Inline(string text)
    {
        var escaped = EscapeHtml(text);
        escaped = LinkRegex().Replace(escaped, "<a href=\"$2\">$1</a>");
        escaped = BoldRegex().Replace(escaped, "<b>$1</b>");
        escaped = CodeRegex().Replace(escaped, "<code>$1</code>");
        return escaped;
    }

    /// <summary>Режет текст на куски не длиннее лимита, сохраняя целостность блоков кода.</summary>
    public static IReadOnlyList<string> SplitForTelegram(string text, int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 200, 4096);
        if (string.IsNullOrEmpty(text)) return [""];
        if (text.Length <= maxChars) return [text];

        var chunks = new List<string>();
        var current = new StringBuilder();
        var fenceOpen = false;
        var fenceLang = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var lineFence = CountFenceMarkers(line);

            if (current.Length + line.Length + 1 > maxChars && current.Length > 0)
            {
                if (fenceOpen) current.Append("\n```");
                chunks.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
                if (fenceOpen) current.Append("```").Append(fenceLang).Append('\n');
            }

            while (line.Length > maxChars)
            {
                var take = maxChars - current.Length - 1;
                if (take < 50)
                {
                    if (fenceOpen) current.Append("\n```");
                    chunks.Add(current.ToString().TrimEnd('\n'));
                    current.Clear();
                    if (fenceOpen) current.Append("```").Append(fenceLang).Append('\n');
                    take = maxChars - current.Length - 1;
                }

                current.Append(line.AsSpan(0, take)).Append('\n');
                line = line[take..];

                if (fenceOpen) current.Append("```");
                chunks.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
                if (fenceOpen) current.Append("```").Append(fenceLang).Append('\n');
            }

            if (lineFence % 2 == 1)
            {
                if (!fenceOpen)
                {
                    var lang = line.TrimStart('`').Trim();
                    fenceLang = lang.Length is > 0 and < 24 && !lang.Contains(' ') ? lang : "";
                }
                fenceOpen = !fenceOpen;
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            if (fenceOpen) current.Append("\n```");
            chunks.Add(current.ToString().TrimEnd('\n'));
        }

        return chunks.Count == 0 ? [""] : chunks;
    }

    private static int CountFenceMarkers(string line)
    {
        var count = 0;
        var index = 0;
        while ((index = line.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 3;
        }
        return count;
    }

    public static string Truncate(string text, int max, string suffix = "…")
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..Math.Max(0, max - suffix.Length)] + suffix;
    }

    public static string HumanBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*([^*\r\n]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex CodeRegex();
}
