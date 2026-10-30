using Markdig;

namespace NotepadSharp.Services;

/// <summary>
/// Markdown 渲染服务：将 Markdown 文本转换为格式化的预览内容
/// </summary>
public static class MarkdownService
{
    /// <summary>
    /// Markdig 管道构建器（静态常驻，只为集中声明「开哪些扩展」）：
    /// UseAdvancedExtensions 一次性打开常用 GFM 扩展，后面几个 Use 是把预览里真正依赖的几项显式点名，
    /// 好处是读者一眼能看出支持表格 / 任务清单 / 标题自动 ID，而不必去查 advanced 包含哪些。
    /// 注意构建器本身不是渲染用的管道，它只负责配置，实际管道见 <see cref="_pipeline"/>。
    /// </summary>
    private static readonly MarkdownPipelineBuilder _pipelineBuilder = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmphasisExtras()   // 支持 ~~删除线~~、==高亮==、上下标等额外强调语法
        .UsePipeTables()       // 支持 | 列 | 列 | 形式的表格
        .UseTaskLists()        // 支持 - [ ] / - [x] 任务清单
        .UseAutoIdentifiers(); // 为标题生成锚点 ID，便于目录/跳转

    /// <summary>
    /// 取一份可直接用的渲染管道。每次访问都调 Build() 重新编译一份：
    /// Markdig 的管道对象本身是不可变的，重复 Build 结果等价，只是多一次分配。
    /// </summary>
    private static MarkdownPipeline _pipeline => _pipelineBuilder.Build();

    /// <summary>
    /// 将 Markdown 转换为 HTML
    /// 用途有两处：「转 HTML」功能把结果当新标签页内容插入，以及纯文本预览的中间产物。
    /// </summary>
    /// <param name="markdown">原始 Markdown 文本；空串或 null 直接返回空串，不进渲染管道</param>
    /// <returns>Markdig 生成的 HTML 片段（不含 &lt;html&gt; 外壳，可直接塞进浏览器控件或再降级为纯文本）</returns>
    public static string RenderToHtml(string markdown)
    {
        // 空输入不进管道：Markdig 对空串也要走一遍解析，白耗一次 Build()
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        return Markdown.ToHtml(markdown, _pipeline);
    }

    /// <summary>
    /// 将 Markdown 转换为纯文本预览（用于 TextBlock 显示）
    /// ND2 的 Markdown 预览走 mdrenderer 做富文本，这里对齐文档里标注的现状是「纯文本预览」，
    /// 即先渲成 HTML 再把标签剥掉，只保留结构与换行，供不需要滚动条/富文本的窄面板使用。
    /// </summary>
    /// <param name="markdown">原始 Markdown 文本；空串或 null 返回空串</param>
    /// <returns>去标签后的可读文本：块级标签处换行、HTML 实体还原成字符</returns>
    public static string RenderToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        // 两步走：Markdown → HTML → 纯文本，复用同一套 Markdig 语义，不必自己实现 Markdown 解析
        var html = RenderToHtml(markdown);
        return StripHtml(html);
    }

    /// <summary>
    /// 把 HTML 片段降级成纯文本：遇到块级标签补一个换行，其余标签整段跳过，并把常见实体还原。
    /// 逐字符扫描而不用正则，是为了避免「贪婪匹配吃掉正文」，也省掉每次预览都编译正则的开销。
    /// 只做预览够用：不解析属性、不处理 &lt;script&gt;/&lt;style&gt; 内部文本，也认不全所有实体。
    /// </summary>
    /// <param name="html">RenderToHtml 生成的 HTML；空串直接返回空串</param>
    /// <returns>用于 TextBlock 展示的纯文本</returns>
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
                // 找不到闭合的 >：说明后面是残缺标签（或被截断的 HTML），剩下的内容整体放弃
                if (end < 0) break;

                var tag = html.Substring(i, end - i + 1);
                // Add newline for block elements
                // 只认开始标签（</p> 这类结束标签以 "</" 开头，不匹配下面的前缀），避免段落被拆成两个空行
                if (tag.StartsWith("<h") || tag.StartsWith("<p") || tag.StartsWith("<div") ||
                    tag.StartsWith("<li") || tag.StartsWith("<br") || tag.StartsWith("<table") ||
                    tag.StartsWith("<tr") || tag.StartsWith("<blockquote"))
                {
                    result.Append('\n');
                }
                // 整段标签跳过不输出，指针移到 > 之后继续处理正文
                i = end + 1;
            }
            else
            {
                // Decode basic HTML entities
                if (html[i] == '&')
                {
                    int semi = html.IndexOf(';', i);
                    // 后面根本没有 ; 或实体长得离谱（>6 个字符）：当作普通 & 输出，
                    // 只往后走一位，免得把正文里的 & 误吞
                    if (semi < 0 || semi - i > 6)
                    {
                        result.Append(html[i]);
                        i++;
                        continue;
                    }
                    // 已知实体还原成正文里真正的字符；未知实体（如 &nbsp;）原样保留，
                    // 让预览至少能看出这里有个转义序列而不是乱码
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
                    // 普通正文字符：直接搬运
                    result.Append(html[i]);
                    i++;
                }
            }
        }
        return result.ToString();
    }
}
