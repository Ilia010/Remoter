using Remoter.App;

namespace Remoter.Tests;

public class TextUtilTests
{
    [Fact]
    public void ToTelegramHtml_ConvertsBoldAndInlineCode()
    {
        var html = TextUtil.ToTelegramHtml("**жирный** и `код`");

        Assert.Contains("<b>жирный</b>", html);
        Assert.Contains("<code>код</code>", html);
    }

    [Fact]
    public void ToTelegramHtml_EscapesHtmlCharacters()
    {
        var html = TextUtil.ToTelegramHtml("if (a < b && c > d)");

        Assert.Contains("&lt;", html);
        Assert.Contains("&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void ToTelegramHtml_TurnsFenceIntoPreCode()
    {
        var html = TextUtil.ToTelegramHtml("```csharp\nvar x = a < b;\n```");

        Assert.Contains("<pre><code>", html);
        Assert.Contains("var x = a &lt; b;", html);
        Assert.DoesNotContain("csharp", html);
    }

    [Fact]
    public void ToTelegramHtml_ConvertsMarkdownLink()
    {
        var html = TextUtil.ToTelegramHtml("[сайт](https://example.com)");

        Assert.Contains("<a href=\"https://example.com\">сайт</a>", html);
    }

    [Fact]
    public void SplitForTelegram_KeepsEveryChunkWithinLimit()
    {
        var text = string.Join("\n", Enumerable.Repeat("строка с текстом", 500));

        var chunks = TextUtil.SplitForTelegram(text, 500);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 500, $"кусок длиной {chunk.Length} > 500"));
    }

    [Fact]
    public void SplitForTelegram_BalancesCodeFencesInEveryChunk()
    {
        var body = string.Join("\n", Enumerable.Repeat("Console.WriteLine(\"hello\");", 200));
        var text = "```csharp\n" + body + "\n```";

        var chunks = TextUtil.SplitForTelegram(text, 600);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            var markers = CountFenceMarkers(chunk);
            Assert.True(markers % 2 == 0, $"незакрытый блок кода в куске: {chunk[..Math.Min(80, chunk.Length)]}");
        });
    }

    [Fact]
    public void SplitForTelegram_ShortTextStaysSingleChunk()
    {
        var chunks = TextUtil.SplitForTelegram("короткий ответ", 3800);

        Assert.Single(chunks);
        Assert.Equal("короткий ответ", chunks[0]);
    }

    [Fact]
    public void SplitForTelegram_EmptyTextReturnsSingleEmptyChunk()
    {
        Assert.Equal([""], TextUtil.SplitForTelegram("", 3800));
    }

    [Fact]
    public void HumanBytes_UsesBinaryUnits()
    {
        Assert.Equal("512 Б", TextUtil.HumanBytes(512));
        Assert.Equal("1 КБ", TextUtil.HumanBytes(1024));
        Assert.Equal("1,5 МБ", TextUtil.HumanBytes((long)(1.5 * 1024 * 1024)));
    }

    [Fact]
    public void Truncate_AddsEllipsisOnlyWhenNeeded()
    {
        Assert.Equal("abc", TextUtil.Truncate("abc", 10));
        Assert.Equal("abcd…", TextUtil.Truncate("abcdef", 5));
    }

    [Fact]
    public void HtmlToPlain_StripsTagsAndUnescapesEntities()
    {
        var plain = TextUtil.HtmlToPlain("<b>жирный</b> &lt;tag&gt; &amp; ok");

        Assert.Equal("жирный <tag> & ok", plain);
    }

    [Fact]
    public void SplitByLines_ReturnsNullWhenLineExceedsLimit()
    {
        Assert.Null(TextUtil.SplitByLines(new string('a', 600), 500));
    }

    [Fact]
    public void SplitByLines_KeepsTagsWhole()
    {
        var html = string.Join("\n", Enumerable.Repeat("<b>строка</b>", 200));

        var chunks = TextUtil.SplitByLines(html, 500);

        Assert.NotNull(chunks);
        Assert.True(chunks!.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 500));
        Assert.All(chunks, chunk =>
            Assert.Equal(CountOccurrences(chunk, "<b>"), CountOccurrences(chunk, "</b>")));
    }

    [Fact]
    public void SplitByLines_ShortHtmlStaysSingleChunk()
    {
        Assert.Equal(["<b>ok</b>"], TextUtil.SplitByLines("<b>ok</b>", 500));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static int CountFenceMarkers(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 3;
        }
        return count;
    }
}
