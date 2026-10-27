using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace NotepadSharp.Services;

/// <summary>
/// 主题服务：管理编辑器配色和 WPF 界面主题
/// </summary>
public static class ThemeService
{
    public enum ThemeMode
    {
        Light,
        Dark,
    }

    public static ThemeMode CurrentTheme { get; private set; } = ThemeMode.Light;

    public static void SetTheme(ThemeMode theme)
    {
        CurrentTheme = theme;
    }

    public static void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
    }

    private static bool IsDark => CurrentTheme == ThemeMode.Dark;

    // ==================== WPF Media.Color (XAML) ====================

    public static WpfColor WpfBackground => IsDark
        ? WpfColor.FromRgb(0x1E, 0x1E, 0x1E)
        : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);

    public static WpfColor WpfPanelBackground => IsDark
        ? WpfColor.FromRgb(0x25, 0x25, 0x26)
        : WpfColor.FromRgb(0xF5, 0xF5, 0xF5);

    public static WpfColor WpfTextColor => IsDark
        ? WpfColor.FromRgb(0xD4, 0xD4, 0xD4)
        : WpfColor.FromRgb(0x1E, 0x1E, 0x1E);

    public static WpfColor WpfTextSecondary => IsDark
        ? WpfColor.FromRgb(0x9D, 0x9D, 0x9D)
        : WpfColor.FromRgb(0x6A, 0x6A, 0x6A);

    public static WpfColor WpfBorder => IsDark
        ? WpfColor.FromRgb(0x3C, 0x3C, 0x3C)
        : WpfColor.FromRgb(0xE0, 0xE0, 0xE0);

    public static WpfColor WpfAccent => WpfColor.FromRgb(0x00, 0x78, 0xD4);

    public static WpfColor WpfSelection => IsDark
        ? WpfColor.FromRgb(0x2D, 0x4D, 0x7E)
        : WpfColor.FromRgb(0xAD, 0xD6, 0xFF);

    // ==================== Drawing.Color (Scintilla) ====================

    private static DrawingColor D(byte r, byte g, byte b) => DrawingColor.FromArgb(255, r, g, b);

    // ===== 调色板 =====
    // 亮色对齐 QScintilla 默认色，暗色对齐 Monokai 主题

    private static DrawingColor Comment => IsDark ? D(0x75, 0x71, 0x5E) : D(0x00, 0x7F, 0x00);      // 注释：绿
    private static DrawingColor CommentDocC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x3F, 0x70, 0x3F);   // 文档注释
    private static DrawingColor DocKeywordC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x30, 0x60, 0xA0);  // 文档关键字
    private static DrawingColor DocKeywordErrC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x80, 0x40, 0x20);
    private static DrawingColor NumberC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0xFF, 0x80, 0x00);      // 数字：橙
    private static DrawingColor KeywordC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0x00, 0xFF);     // 关键字：蓝
    private static DrawingColor TypeC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x2B, 0x91, 0xAF);        // 类型：青
    private static DrawingColor StringC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x7F);      // 字符串：紫
    private static DrawingColor VerbatimC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x00);    // 逐字字符串
    private static DrawingColor RegexC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x3F, 0x7F, 0x3F);        // 正则
    private static DrawingColor OperatorC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0x80);    // 运算符：深蓝
    private static DrawingColor IdentifierC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0x1E, 0x1E, 0x1E);  // 标识符
    private static DrawingColor PreprocC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x7F, 0x7F, 0x00);     // 预处理：橄榄
    private static DrawingColor FuncC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x30, 0x60, 0xA0);        // 函数/成员
    private static DrawingColor TagC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0x80);         // 标签
    private static DrawingColor AttrC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x7F, 0x00, 0x00);        // 属性
    private static DrawingColor TaskMarkerC => D(0xBE, 0x07, 0xFF);                                 // TODO 标记
    private static DrawingColor ErrorC => D(0xF4, 0x47, 0x47);

    // ===== HTML / XML / JSON / CSS 专用 =====
    private static DrawingColor HtmlTagC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0xFF);      // 标签：蓝
    private static DrawingColor HtmlAttrC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0xFF, 0x00, 0x00);     // 属性：红
    private static DrawingColor HtmlNumC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x7F);      // 数字：青
    private static DrawingColor HtmlCommentC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x80, 0x80, 0x00);  // 注释：橄榄
    private static DrawingColor HtmlEntityC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x80, 0x00, 0x80);   // 实体：紫
    private static DrawingColor HtmlValueC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0xFF, 0x00, 0xFF);    // 属性值：品红
    private static DrawingColor JsKeywordC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x80, 0x00, 0xFF);    // HTML 内嵌 JS 关键字
    private static DrawingColor JsSymC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0x00, 0x00, 0x00);
    private static DrawingColor JsonPropC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x88, 0x0A, 0xE8);      // JSON 属性名
    private static DrawingColor JsonStrC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x00);       // JSON 字符串
    private static DrawingColor JsonNumC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x7F);       // JSON 数字
    private static DrawingColor JsonKwC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x0B, 0xCE, 0xA7);        // JSON 关键字
    private static DrawingColor CssDefaultC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0xFF, 0x00, 0x80);   // CSS 选择器
    private static DrawingColor CssTagC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x00, 0x00, 0x7F);
    private static DrawingColor CssPropC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0x40, 0xE0);      // CSS 属性
    private static DrawingColor CssProp2C => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0xA0, 0xE0);
    private static DrawingColor CssValueC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x7F);     // CSS 值
    private static DrawingColor CssIdC => IsDark ? D(0xFD, 0x97, 0x1F) : D(0x00, 0x7F, 0x7F);        // #id
    private static DrawingColor ImportantC => IsDark ? D(0xFD, 0x97, 0x1F) : D(0xFF, 0x80, 0x00);    // !important
    private static DrawingColor AtRuleC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x7F, 0x7F, 0x00);       // @规则

    /// <summary>
    /// 将主题应用到 Scintilla 编辑器（含语法高亮样式）
    /// </summary>
    public static void ApplyToScintilla(ScintillaNET.Scintilla sci, string? lexerName = null)
    {
        sci.StyleResetDefault();
        var def = sci.Styles[ScintillaNET.Style.Default];
        def.Font = string.IsNullOrWhiteSpace(SettingsService.Current.FontFamily) ? "Consolas" : SettingsService.Current.FontFamily;
        def.Size = SettingsService.Current.FontSize > 0 ? (int)Math.Round(SettingsService.Current.FontSize) : 10;
        def.BackColor = IsDark ? D(0x1E, 0x1E, 0x1E) : D(0xFF, 0xFF, 0xFF);
        def.ForeColor = IdentifierC;
        sci.StyleClearAll();

        var lineNumber = sci.Styles[ScintillaNET.Style.LineNumber];
        lineNumber.BackColor = IsDark ? D(0x25, 0x25, 0x26) : D(0xF0, 0xF0, 0xF0);
        lineNumber.ForeColor = IsDark ? D(0x85, 0x85, 0x85) : D(0x88, 0x88, 0x88);

        sci.CaretLineVisible = true;
        sci.CaretLineBackColor = IsDark ? D(0x2A, 0x2D, 0x2E) : D(0xE8, 0xF2, 0xFF);

        sci.SetSelectionBackColor(true, IsDark ? D(0x2D, 0x4D, 0x7E) : D(0xAD, 0xD6, 0xFF));
        sci.SetSelectionForeColor(true, IsDark ? D(0xFF, 0xFF, 0xFF) : D(0x00, 0x00, 0x00));

        sci.CaretForeColor = IsDark ? D(0xAE, 0xAF, 0xAD) : D(0x00, 0x00, 0x00);

        var braceLight = sci.Styles[ScintillaNET.Style.BraceLight];
        braceLight.BackColor = IsDark ? D(0x3B, 0x51, 0x6D) : D(0xD6, 0xEA, 0xFF);
        braceLight.ForeColor = IsDark ? D(0xFF, 0xFF, 0xFF) : D(0x00, 0x00, 0x00);
        sci.Styles[ScintillaNET.Style.BraceBad].ForeColor = ErrorC;

        var markerColor = IsDark ? D(0xA0, 0xA0, 0xA0) : D(0x90, 0x90, 0x90);
        for (int i = 25; i <= 31; i++)
        {
            sci.Markers[i].SetForeColor(IsDark ? D(0x1E, 0x1E, 0x1E) : D(0xF5, 0xF5, 0xF5));
            sci.Markers[i].SetBackColor(markerColor);
        }

        ApplySyntaxColors(sci, lexerName);
    }

    private static void ApplySyntaxColors(ScintillaNET.Scintilla sci, string? lexerName)
    {
        string language;
        if (!string.IsNullOrEmpty(lexerName))
        {
            language = lexerName.ToLowerInvariant();
        }
        else
        {
            try { language = (sci.LexerLanguage ?? string.Empty).ToLowerInvariant(); }
            catch { language = string.Empty; }
        }

        switch (language)
        {
            case "cpp":
            case "javascript":
            case "typescript":
                ApplyCppColors(sci); break;
            case "python": ApplyPythonColors(sci); break;
            case "hypertext":
            case "xml": ApplyHtmlColors(sci); break;
            case "json": ApplyJsonColors(sci); break;
            case "css": ApplyCssColors(sci); break;
            case "sql": ApplySqlColors(sci); break;
            case "markdown": ApplyMarkdownColors(sci); break;
            case "yaml": ApplyYamlColors(sci); break;
            case "bash": ApplyBashColors(sci); break;
            case "batch":
            case "powershell": ApplyBatchColors(sci); break;
            case "props": ApplyPropsColors(sci); break;
            case "r": ApplyRColors(sci); break;
            case "diff": ApplyDiffColors(sci); break;
            default: ApplyGenericColors(sci); break;
        }
    }

    // SCE_C_* / C# / C++ / Java / TS / Kotlin / Swift
    private static void ApplyCppColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                  // 块注释
        SetStyle(sci, 2, Comment);                  // 行注释
        SetStyle(sci, 3, CommentDocC);              // 文档注释
        SetStyle(sci, 4, NumberC);                  // 数字
        SetStyle(sci, 5, KeywordC, bold: true);     // 关键字
        SetStyle(sci, 6, StringC);                  // 字符串
        SetStyle(sci, 7, StringC);                  // 字符
        SetStyle(sci, 8, StringC);                  // UUID
        SetStyle(sci, 9, PreprocC);                 // 预处理器
        SetStyle(sci, 10, OperatorC);               // 操作符
        SetStyle(sci, 11, IdentifierC);             // 标识符
        SetStyle(sci, 12, OperatorC);               // 未结束字符串
        SetStyle(sci, 13, VerbatimC);               // 逐字字符串
        SetStyle(sci, 14, RegexC);                  // 正则
        SetStyle(sci, 15, CommentDocC);             // 文档行注释
        SetStyle(sci, 16, TypeC, bold: true);       // 二级关键字 / 类型名
        SetStyle(sci, 17, DocKeywordC);             // 文档关键字
        SetStyle(sci, 18, DocKeywordErrC);          // 文档关键字错误
        SetStyle(sci, 19, TypeC);                   // 全局类
        SetStyle(sci, 20, StringC);                 // 原始字符串
        SetStyle(sci, 21, VerbatimC);               // 三引号字符串
        SetStyle(sci, 22, VerbatimC);               // 哈希引号字符串
        SetStyle(sci, 26, TaskMarkerC, bold: true); // TODO 标记
    }

    // SCE_P_*
    private static void ApplyPythonColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 行注释
        SetStyle(sci, 2, Comment);                 // 块注释
        SetStyle(sci, 3, Comment);                 // 注释 NT
        SetStyle(sci, 4, NumberC);                 // 数字
        SetStyle(sci, 5, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 6, StringC);                 // 三引号字符串
        SetStyle(sci, 7, StringC);                 // 三双引号字符串
        SetStyle(sci, 8, TypeC, bold: true);       // 类名
        SetStyle(sci, 9, FuncC);                   // 函数名
        SetStyle(sci, 10, OperatorC);              // 操作符
        SetStyle(sci, 11, IdentifierC);            // 标识符
        SetStyle(sci, 12, Comment);                // 块注释 NT
        SetStyle(sci, 13, StringC);                // 三引号 NT
        SetStyle(sci, 14, StringC);                // 三双引号 NT
        SetStyle(sci, 16, FuncC);                  // 装饰器
    }

    // SCE_H_* （HTML / XML，含内嵌 JS）
    private static void ApplyHtmlColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 0, IdentifierC);              // 默认
        SetStyle(sci, 1, HtmlTagC, bold: true);     // 标签
        SetStyle(sci, 2, ErrorC);                   // 未知标签
        SetStyle(sci, 3, HtmlAttrC);                // 属性
        SetStyle(sci, 4, ErrorC);                   // 未知属性
        SetStyle(sci, 5, HtmlNumC);                 // 数字
        SetStyle(sci, 6, StringC);                  // 双引号字符串
        SetStyle(sci, 7, StringC);                  // 单引号字符串
        SetStyle(sci, 8, HtmlEntityC);              // 其他
        SetStyle(sci, 9, HtmlCommentC);             // 注释
        SetStyle(sci, 10, HtmlEntityC);             // 实体
        SetStyle(sci, 11, HtmlTagC);                // 标签结束
        SetStyle(sci, 12, HtmlTagC, bold: true);    // XML 开始
        SetStyle(sci, 13, HtmlTagC, bold: true);    // XML 结束
        SetStyle(sci, 14, HtmlTagC);                // 脚本
        SetStyle(sci, 17, HtmlCommentC);            // CDATA
        SetStyle(sci, 18, HtmlCommentC);            // 处理指令
        SetStyle(sci, 19, HtmlValueC);              // 属性值
        SetStyle(sci, 20, HtmlCommentC);            // 扩展注释

        // 内嵌 JavaScript（SCE_HJ_* = 40..52）
        SetStyle(sci, 41, IdentifierC);             // JS 默认
        SetStyle(sci, 42, Comment);                 // JS 块注释
        SetStyle(sci, 43, Comment);                 // JS 行注释
        SetStyle(sci, 44, CommentDocC);             // JS 文档注释
        SetStyle(sci, 45, HtmlNumC);                // JS 数字
        SetStyle(sci, 46, IdentifierC);             // JS 标识符
        SetStyle(sci, 47, JsKeywordC, bold: true);  // JS 关键字
        SetStyle(sci, 48, StringC);                 // JS 双引号字符串
        SetStyle(sci, 49, StringC);                 // JS 单引号字符串
        SetStyle(sci, 50, JsSymC);                  // JS 符号
        SetStyle(sci, 51, StringC);                 // JS 未结束字符串
        SetStyle(sci, 52, RegexC);                  // JS 正则
    }

    // SCE_JSON_*
    private static void ApplyJsonColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 0, IdentifierC);              // 默认
        SetStyle(sci, 1, JsonNumC);                 // 数字
        SetStyle(sci, 2, JsonStrC);                 // 字符串
        SetStyle(sci, 3, ErrorC);                   // 未结束字符串
        SetStyle(sci, 4, JsonPropC, bold: true);    // 属性名
        SetStyle(sci, 5, PreprocC);                 // 转义序列
        SetStyle(sci, 6, Comment);                  // 行注释
        SetStyle(sci, 7, Comment);                  // 块注释
        SetStyle(sci, 8, OperatorC);                // 操作符
        SetStyle(sci, 9, HtmlTagC);                 // IRI
        SetStyle(sci, 10, HtmlValueC);              // IRI 紧凑
        SetStyle(sci, 11, JsonKwC, bold: true);     // 关键字 true/false/null
        SetStyle(sci, 12, ErrorC);                  // LD 关键字
        SetStyle(sci, 13, ErrorC);                  // 错误
    }

    // SCE_CSS_*
    private static void ApplyCssColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 0, CssDefaultC);              // 默认（选择器）
        SetStyle(sci, 1, CssTagC, bold: true);      // 标签
        SetStyle(sci, 2, CssTagC);                  // class
        SetStyle(sci, 3, HtmlAttrC);                // 伪类
        SetStyle(sci, 4, ErrorC);                   // 未知伪类
        SetStyle(sci, 5, OperatorC);                // 操作符
        SetStyle(sci, 6, CssPropC);                 // CSS1 属性
        SetStyle(sci, 7, ErrorC);                   // 未知属性
        SetStyle(sci, 8, CssValueC);                // 值
        SetStyle(sci, 9, Comment);                  // 注释
        SetStyle(sci, 10, CssIdC);                  // #id
        SetStyle(sci, 11, ImportantC, bold: true);  // !important
        SetStyle(sci, 12, AtRuleC, bold: true);     // @规则
        SetStyle(sci, 13, AtRuleC);                 // @media
        SetStyle(sci, 14, CssProp2C);               // CSS2 属性
        SetStyle(sci, 15, HtmlAttrC);               // 扩展伪类
        SetStyle(sci, 16, CssProp2C);               // 扩展标识符
    }

    // SCE_SQL_*
    private static void ApplySqlColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, Comment);                 // 行注释
        SetStyle(sci, 3, Comment);                 // 文档注释
        SetStyle(sci, 4, NumberC);                 // 数字
        SetStyle(sci, 5, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 6, StringC);                 // 字符串
        SetStyle(sci, 7, StringC);                 // 字符
        SetStyle(sci, 13, IdentifierC);            // 标识符
    }

    // SCE_MARKDOWN_*
    private static void ApplyMarkdownColors(ScintillaNET.Scintilla sci)
    {
        for (int i = 3; i <= 9; i++) SetStyle(sci, i, TagC, bold: true);  // 各级标题
        SetStyle(sci, 10, TypeC, bold: true);      // 粗体
        SetStyle(sci, 11, TypeC, bold: true);
        SetStyle(sci, 12, TypeC, italic: true);    // 斜体
        SetStyle(sci, 13, TypeC, italic: true);
        for (int i = 14; i <= 16; i++) SetStyle(sci, i, StringC);         // 代码
        SetStyle(sci, 17, KeywordC);               // 无序列表
        SetStyle(sci, 18, KeywordC);               // 有序列表
        SetStyle(sci, 19, Comment);                // 引用
        SetStyle(sci, 20, StringC);                // 删除线
        SetStyle(sci, 21, PreprocC);               // 分隔线
        SetStyle(sci, 22, TagC, underline: true);  // 链接
    }

    // SCE_YAML_*
    private static void ApplyYamlColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, IdentifierC);             // 标识符
        SetStyle(sci, 3, KeywordC);                // 关键字
        SetStyle(sci, 4, NumberC);                 // 数字
        SetStyle(sci, 5, FuncC);                   // 引用
        SetStyle(sci, 6, PreprocC, bold: true);    // 文档分隔符
        SetStyle(sci, 7, StringC);                 // 文本
        SetStyle(sci, 8, ErrorC);                  // 错误
        SetStyle(sci, 9, OperatorC);               // 操作符
        SetStyle(sci, 10, TypeC);                  // 日期
    }

    // SCE_SH_*
    private static void ApplyBashColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, ErrorC);                  // 错误
        SetStyle(sci, 2, Comment);                 // 注释
        SetStyle(sci, 3, NumberC);                 // 数字
        SetStyle(sci, 4, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 5, StringC);                 // 字符串
        SetStyle(sci, 6, StringC);                 // 字符
        SetStyle(sci, 7, OperatorC);               // 操作符
        SetStyle(sci, 8, IdentifierC);             // 标识符
        SetStyle(sci, 10, FuncC);                  // 参数 / 变量
        SetStyle(sci, 11, FuncC);                  // 反引号
    }

    // SCE_BAT_*
    private static void ApplyBatchColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, KeywordC, bold: true);    // 词
        SetStyle(sci, 5, FuncC);                   // 命令
        SetStyle(sci, 8, StringC);                 // 字符串
    }

    // SCE_R_*
    private static void ApplyRColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 3, KeywordC);                // 基础关键字
        SetStyle(sci, 4, TypeC);                   // 其它关键字
        SetStyle(sci, 5, NumberC);                 // 数字
        SetStyle(sci, 6, StringC);                 // 字符串
        SetStyle(sci, 7, StringC);                 // 字符串2
        SetStyle(sci, 8, OperatorC);               // 操作符
        SetStyle(sci, 10, FuncC);                  // 中缀运算符 %in% 等
        SetStyle(sci, 11, ErrorC);                 // 未闭合中缀
    }

    // SCE_PROPS_*
    private static void ApplyPropsColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, TagC, bold: true);        // 节
        SetStyle(sci, 3, OperatorC);               // 赋值符
        SetStyle(sci, 5, TypeC);                   // 键
    }

    // SCE_DIFF_*
    private static void ApplyDiffColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 3, TagC, bold: true);        // 文件头
        SetStyle(sci, 5, ErrorC);                  // 删除行
        SetStyle(sci, 6, NumberC);                 // 新增行
    }

    // 通用：适用于大多数简单 lexer
    private static void ApplyGenericColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);
        SetStyle(sci, 2, Comment);
        SetStyle(sci, 3, NumberC);
        SetStyle(sci, 4, KeywordC, bold: true);
        SetStyle(sci, 5, KeywordC, bold: true);
        SetStyle(sci, 6, StringC);
        SetStyle(sci, 7, StringC);
        SetStyle(sci, 9, PreprocC);
        SetStyle(sci, 10, OperatorC);
        SetStyle(sci, 11, IdentifierC);
    }

    private static void SetStyle(ScintillaNET.Scintilla sci, int index, DrawingColor fore,
        bool bold = false, bool italic = false, bool underline = false)
    {
        var style = sci.Styles[index];
        style.ForeColor = fore;
        if (bold) style.Bold = true;
        if (italic) style.Italic = true;
        if (underline) style.Underline = true;
    }

    // ==================== 编码映射 ====================

    public static int GetCodePage(System.Text.Encoding encoding)
    {
        if (encoding is System.Text.UTF8Encoding)
            return 65001;
        if (encoding == System.Text.Encoding.Unicode)
            return 1200;
        if (encoding == System.Text.Encoding.BigEndianUnicode)
            return 1201;
        return encoding.CodePage;
    }
}
