using Markdig;

namespace NotepadSharp.Services;

/// <summary>
/// Markdown 渲染服务：将 Markdown 文本转换为格式化的预览内容
/// </summary>
public static class MarkdownService
{
    private static readonly MarkdownPipelineBuilder _pipelineBuilder = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoIdentifiers();

    private static MarkdownPipeline _pipeline => _pipelineBuilder.Build();

    /// <summary>
    /// 将 Markdown 转换为 HTML
    /// </summary>
    public static string RenderToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        return Markdown.ToHtml(markdown, _pipeline);
    }

    /// <summary>
    /// 将 Markdown 转换为纯文本预览（用于 TextBlock 显示）
    /// </summary>
    public static string RenderToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var html = RenderToHtml(markdown);
        return StripHtml(html);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                // Check for newline in block elements
                int end = html.IndexOf('>', i);
                if (end < 0) break;

                var tag = html.Substring(i, end - i + 1);
                // Add newline for block elements
                if (tag.StartsWith("<h") || tag.StartsWith("<p") || tag.StartsWith("<div") ||
                    tag.StartsWith("<li") || tag.StartsWith("<br") || tag.StartsWith("<table") ||
                    tag.StartsWith("<tr") || tag.StartsWith("<blockquote"))
                {
                    result.Append('\n');
                }
                i = end + 1;
            }
            else
            {
                // Decode basic HTML entities
                if (html[i] == '&')
                {
                    int semi = html.IndexOf(';', i);
                    if (semi < 0 || semi - i > 6)
                    {
                        result.Append(html[i]);
                        i++;
                        continue;
                    }
                    var entity = html.Substring(i, semi - i + 1);
                    result.Append(entity switch
                    {
                        "&lt;" => "<",
                        "&gt;" => ">",
                        "&amp;" => "&",
                        "&quot;" => "\"",
                        "&#39;" => "'",
                        _ => entity,
                    });
                    i = semi + 1;
                }
                else
                {
                    result.Append(html[i]);
                    i++;
                }
            }
        }
        return result.ToString();
    }
}
