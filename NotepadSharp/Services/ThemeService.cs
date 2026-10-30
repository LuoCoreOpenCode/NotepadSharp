// 本类要同时喂两套图形栈：WPF 可视树（XAML/画刷）认 System.Windows.Media.Color，
// 而 Scintilla 原生控件的样式表 / indicator / marker 走 Win32 COLORREF，封装层收的是
// System.Drawing.Color（GDI+）。两个类型同名 Color，所以各起一个别名，省得每处都写全限定名。
using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace NotepadSharp.Services;

/// <summary>
/// 主题服务：管理编辑器配色和 WPF 界面主题
/// </summary>
/// <remarks>
/// 职责分三块：
/// 1）<see cref="ThemeMode"/> + <see cref="CurrentTheme"/> 记住当前是亮色还是暗色（全局单例状态，
///    由主窗口在启动/切主题/保存设置时驱动，见 MainWindow 的 SetTheme / ApplyThemeToUI）；
/// 2）一组 <c>Wpf*</c> 颜色属性，把同样的色值以 WPF Color 形式暴露给代码侧动态构造画刷的场景
///    （界面静态部分的颜色走 Resources/ThemeLight.xaml 与 ThemeDark.xaml 资源字典，两边取值一致）；
/// 3）Scintilla 侧的完整着色管线 <see cref="ApplyToScintilla"/>：默认样式、行号边距、当前行高亮、
///    选区、光标、括号配对 indicator、折叠 marker，再按词法器名分派到各语言的样式表。
/// 亮色基准取自参考实现（ND2）的默认配色（QScintilla / stylers.xml Default 一路的观感），
/// 暗色基准是 Monokai 风格的自定义表；两处都是「一套表 + IsDark 三元」而不是两份字典，
/// 所以新增语言样式时只要挂上语义色属性，两个主题自动都有对应颜色。
/// 所有颜色属性都是表达式体（get-only），每次读都按 <see cref="CurrentTheme"/> 现算，
/// 因此调用顺序很重要：<c>SetTheme/ToggleTheme</c> 必须先于 <c>ApplyToScintilla</c>，否则刷出旧主题。
/// </remarks>
public static class ThemeService
{
    /// <summary>
    /// 界面主题档位：只有亮/暗两档（对应参考实现的「默认样式 / 深色样式」两条主线，
    /// 其余 ND2 的 18 套 stylers 皮肤本工具未复刻，见 功能对照.md 的「主题/皮肤」条目）。
    /// </summary>
    public enum ThemeMode
    {
        /// <summary>亮色主题：白底深字，编辑器区沿用 QScintilla 默认高亮色（关键字蓝、注释绿、字符串紫）</summary>
        Light,
        /// <summary>暗色主题：深灰底浅字，编辑器区用 Monokai 倾向的高亮色（关键字亮青、字符串黄）</summary>
        Dark,
    }

    /// <summary>
    /// 当前主题档位。进程级唯一状态：主窗口读它决定加载哪份主题资源字典，
    /// 编辑器读它决定 Scintilla 样式色，设置面板读它决定单选框勾选。
    /// </summary>
    /// <remarks>私有 setter：只能通过 <see cref="SetTheme"/> 或 <see cref="ToggleTheme"/> 改写，
    /// 保证不会有第三处代码绕开「改状态 → 重刷 UI」的顺序。</remarks>
    public static ThemeMode CurrentTheme { get; private set; } = ThemeMode.Light;

    /// <summary>
    /// 显式设定主题档位（只改状态，不刷新界面）。
    /// </summary>
    /// <param name="theme">目标档位：亮色或暗色</param>
    /// <remarks>调用方负责随后刷 UI（主窗口的 <c>ApplyThemeToUI</c> + 每个编辑器的 <c>ApplyTheme</c>）；
    /// 本方法刻意不做任何副作用，方便批量恢复设置时只刷一次。</remarks>
    public static void SetTheme(ThemeMode theme)
    {
        CurrentTheme = theme;
    }

    /// <summary>
    /// 亮暗互换（Ctrl+T 走这里）：只翻转状态，刷新交给调用方。
    /// </summary>
    public static void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
    }

    /// <summary>
    /// 当前是否暗色。本类所有颜色属性都靠它做三元分支，等于「一张表、两套色」的开关。
    /// </summary>
    private static bool IsDark => CurrentTheme == ThemeMode.Dark;

    // ==================== WPF Media.Color (XAML) ====================
    // 下面这组属性是「代码侧出口」：需要在 C# 里临时造画刷（例如自绘控件、动态提示条、
    // 截图导出背景）时用它取色，数值与 Resources/ThemeLight.xaml、ThemeDark.xaml 里的
    // SolidColorBrush 一一对应（同名键：BackgroundBrush / PanelBackground / TextBrush /
    // TextSecondaryBrush / BorderBrush / AccentBrush / SelectionBrush）。
    // 界面的静态部分仍然用 DynamicResource 绑 XAML 键，换主题时靠替换资源字典自动刷新；
    // 这两处的值务必保持同步，否则会出现「菜单是暗色、弹出层是亮色」的割裂。

    /// <summary>
    /// 窗口/内容区主背景色：暗色 0x1E1E1E、亮色纯白。
    /// 对应 XAML 的 BackgroundBrush，用于窗口底色、编辑器文本区背景这类最大面积的底色。
    /// </summary>
    public static WpfColor WpfBackground => IsDark
        ? WpfColor.FromRgb(0x1E, 0x1E, 0x1E)
        : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 面板/工具条背景色：暗色 0x252526、亮色 0xF5F5F5（比主背景略深/略灰，做出层次）。
    /// 对应 XAML 的 PanelBackground，用于菜单条、状态栏、停靠面板、侧边列表等非编辑区。
    /// </summary>
    public static WpfColor WpfPanelBackground => IsDark
        ? WpfColor.FromRgb(0x25, 0x25, 0x26)
        : WpfColor.FromRgb(0xF5, 0xF5, 0xF5);

    /// <summary>
    /// 主文字颜色：暗色 0xD4D4D4（VS 系浅灰）、亮色近黑 0x1E1E1E。
    /// 对应 XAML 的 TextBrush，标题、菜单项、正文标签都用它。
    /// </summary>
    public static WpfColor WpfTextColor => IsDark
        ? WpfColor.FromRgb(0xD4, 0xD4, 0xD4)
        : WpfColor.FromRgb(0x1E, 0x1E, 0x1E);

    /// <summary>
    /// 次要文字颜色：暗色 0x9D9D9D、亮色 0x6A6A6A，用于弱化层级的说明文字。
    /// 对应 XAML 的 TextSecondaryBrush，如状态栏提示、占位符、辅助信息、禁用态标签。
    /// </summary>
    public static WpfColor WpfTextSecondary => IsDark
        ? WpfColor.FromRgb(0x9D, 0x9D, 0x9D)
        : WpfColor.FromRgb(0x6A, 0x6A, 0x6A);

    /// <summary>
    /// 分隔线/控件描边颜色：暗色 0x3C3C3C、亮色浅灰 0xE0E0E0。
    /// 对应 XAML 的 BorderBrush，用于面板边框、分组分隔线、输入框边线。
    /// </summary>
    public static WpfColor WpfBorder => IsDark
        ? WpfColor.FromRgb(0x3C, 0x3C, 0x3C)
        : WpfColor.FromRgb(0xE0, 0xE0, 0xE0);

    /// <summary>
    /// 强调色（品牌蓝 0x0078D4）：刻意不分主题，亮暗两档同色，保证「选中/当前项/链接」的
    /// 辨识度一致。对应 XAML 的 AccentBrush，用于焦点边框、选中项前景、菜单勾选标记。
    /// </summary>
    public static WpfColor WpfAccent => WpfColor.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>
    /// 选区底色：暗色是压低饱和度的蓝 0x2D4D7E（避免在深底上刺眼），亮色是系统常见的
    /// 浅蓝 0xADD6FF。对应 XAML 的 SelectionBrush / SystemColors.HighlightBrushKey，
    /// 与 Scintilla 侧 SetSelectionBackColor 用的是同一组 RGB，不能只改一边。
    /// </summary>
    public static WpfColor WpfSelection => IsDark
        ? WpfColor.FromRgb(0x2D, 0x4D, 0x7E)
        : WpfColor.FromRgb(0xAD, 0xD6, 0xFF);

    // ==================== Drawing.Color (Scintilla) ====================
    // 这一节才是编辑器高亮的真身：Scintilla 样式表要的色值是 GDI+ Color（内部转 COLORREF，
    // alpha 固定 255，因为文本样式不支持半透明）。

    /// <summary>
    /// 构造一个不透明（alpha=255）的 GDI+ 颜色，供下面所有语义色属性和 <see cref="ApplyToScintilla"/> 使用。
    /// </summary>
    /// <param name="r">红通道 0-255</param>
    /// <param name="g">绿通道 0-255</param>
    /// <param name="b">蓝通道 0-255</param>
    /// <returns>Argb(255, r, g, b)；样式表里 alpha 不参与合成，写成 255 只是求稳</returns>
    private static DrawingColor D(byte r, byte g, byte b) => DrawingColor.FromArgb(255, r, g, b);

    // ===== 调色板 =====
    // 亮色对齐 QScintilla 默认色，暗色对齐 Monokai 主题
    // 命名约定：*C = Color 后缀，属性名是「语义」而不是「颜色名」，
    // 所以换主题只改三元表达式里的 RGB，各语言样式表（Apply*Colors）无需跟着改。

    /// <summary>注释前景：亮色纯绿 007F00，暗色 Monokai 灰绿 75715E。用于 C/Python/SQL/YAML/JSON 等的行注释与块注释</summary>
    private static DrawingColor Comment => IsDark ? D(0x75, 0x71, 0x5E) : D(0x00, 0x7F, 0x00);      // 注释：绿
    /// <summary>文档注释前景（/// 、/** */ 这种）：暗色与普通注释同色，亮色用偏暗的青绿 3F703F 与行注释区分</summary>
    private static DrawingColor CommentDocC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x3F, 0x70, 0x3F);   // 文档注释
    /// <summary>文档注释里的关键字（@param / &#92;summary 这类标签名）：亮色钢蓝，暗色仍归到注释灰绿</summary>
    private static DrawingColor DocKeywordC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x30, 0x60, 0xA0);  // 文档关键字
    /// <summary>写错的文档注释关键字（词法器不认识该标签）：暗色用品红提示，亮色用棕色</summary>
    private static DrawingColor DocKeywordErrC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x80, 0x40, 0x20);
    /// <summary>数字字面量前景：亮色橙 FF8000，暗色 Monokai 紫 AE81FF（顺带被 JSON 数字、CSS 数字复用）</summary>
    private static DrawingColor NumberC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0xFF, 0x80, 0x00);      // 数字：橙
    /// <summary>主关键字（if/for/class…，词法器关键字表 0）：亮色纯蓝，暗色亮青，默认加粗</summary>
    private static DrawingColor KeywordC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0x00, 0xFF);     // 关键字：蓝
    /// <summary>二级关键字/类型名（关键字表 1，如 std/string/int 类型）：暗色黄绿，亮色青蓝；也用于 Markdown 粗斜体</summary>
    private static DrawingColor TypeC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x2B, 0x91, 0xAF);        // 类型：青
    /// <summary>字符串与字符常量前景：亮色紫 7F007F，暗色淡黄 E6DB74（Monokai 的字符串色）</summary>
    private static DrawingColor StringC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x7F);      // 字符串：紫
    /// <summary>逐字/原始字符串（Python 三引号、C# @"" 之类的多行原文）：暗色跟数字同紫，亮色跟注释同绿</summary>
    private static DrawingColor VerbatimC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x00);    // 逐字字符串
    /// <summary>正则表达式字面量（/.../）：与字符串区分开，暗色淡黄、亮色灰绿</summary>
    private static DrawingColor RegexC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x3F, 0x7F, 0x3F);        // 正则
    /// <summary>运算符与赋值符（+ = , { } …）：亮色深蓝 000080，暗色品红 F92672</summary>
    private static DrawingColor OperatorC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0x80);    // 运算符：深蓝
    /// <summary>普通标识符/默认文本前景：暗色近白 F8F8F2、亮色近黑 1E1E1E，是样式 Default 和 IDENTIFIER 用的颜色</summary>
    private static DrawingColor IdentifierC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0x1E, 0x1E, 0x1E);  // 标识符
    /// <summary>预处理/转义类：C/C++ 的 #include 行、JSON 的 \\u 转义、Markdown 分隔线、YAML 文档分隔符都用它</summary>
    private static DrawingColor PreprocC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x7F, 0x7F, 0x00);     // 预处理：橄榄
    /// <summary>函数名/成员名（Python def、Bash 参数与反引号、R 中缀、YAML 锚点引用）：暗色黄绿，亮色钢蓝</summary>
    private static DrawingColor FuncC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x30, 0x60, 0xA0);        // 函数/成员
    /// <summary>结构性标记色：Markdown 标题与链接、properties 的节名 [section]、diff 的文件头行共用</summary>
    private static DrawingColor TagC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0x80);         // 标签
    /// <summary>通用属性色（预留）：HTML/CSS 的属性已各自有专用色 HtmlAttrC / CssPropC，目前没有样式表引用它</summary>
    private static DrawingColor AttrC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x7F, 0x00, 0x00);        // 属性
    /// <summary>TODO/FIXME 标记色：不随主题变（深紫蓝，两种底色上都醒目），对应 C++ 词法器的 task marker 样式</summary>
    private static DrawingColor TaskMarkerC => D(0xBE, 0x07, 0xFF);                                 // TODO 标记
    /// <summary>错误/异常槽位色：未闭合字符串、未知标签/属性、语法错误行等一律涂红，不随主题变</summary>
    private static DrawingColor ErrorC => D(0xF4, 0x47, 0x47);

    // ===== 括号配对高亮 =====
    // 参考实现（ND2）只把光标处括号及其配对括号「改字色」，不画底色框；我们照抄这个观感。
    // Scintilla 的样式 34/35（STYLE_BRACELIGHT/STYLE_BRACEBAD）在原生 5.x 上不再渲染，
    // 所以改用 indicator 的 TextFore 样式实现——因此这里要对外暴露槽位号和颜色，
    // 让 EditorControl 的配对算法（<c>UpdateBraceMatch</c>）与主题共用同一份配置。

    /// <summary>括号配对高亮用的指示器编号（8 已被查找高亮占用）</summary>
    /// <remarks>indicator 编号是 Scintilla 的共享资源槽（0..31）：查找高亮占 8、URL 热点另占一号，
    /// 这里挑 9 给「配对成功的括号」，改主题时由 <see cref="ApplyToScintilla"/> 重刷其前景色。</remarks>
    public const int BraceIndicatorIndex = 9;

    /// <summary>括号配不上时改字色用的指示器编号</summary>
    /// <remarks>与 <see cref="BraceIndicatorIndex"/> 成对使用：同一个槽位只能挂一种前景色，
    /// 所以「匹配」和「不匹配」必须占两个编号，由调用方按结果二选一。</remarks>
    public const int BraceBadForeIndicatorIndex = 10;

    /// <summary>配对成功的括号字色（对齐参考：蓝色；暗色主题下用亮青保证可读）</summary>
    /// <remarks>喂给 indicator 的 ForeColor（IndicatorStyle.TextFore 只认前景，Alpha 设 255 才不透明）。</remarks>
    public static DrawingColor BraceMatchedFore => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0x00, 0xFF);

    /// <summary>配不上时的括号字色（对齐参考：红色；暗色主题下用品红保证可读）</summary>
    /// <remarks>典型触发场景：只剩一侧括号、被注释/字符串里的括号干扰配对、或超出配对扫描范围。</remarks>
    public static DrawingColor BraceBadFore => IsDark ? D(0xF9, 0x26, 0x72) : D(0xFF, 0x00, 0x00);

    // ===== HTML / XML / JSON / CSS 专用 =====
    // 这四类词法器（SCLEX_HTML / SCLEX_XML / SCLEX_JSON / SCLEX_CSS）的槽位语义和 C 系完全不同，
    // 上面那套「关键字蓝、字符串紫」的通用色不够用，所以再补一组专用语义色。
    // 命名前缀对应词法器：Html* = SCE_H_*，Js* = SCE_HJ_*（HTML 内嵌 JS），
    // Json* = SCE_JSON_*，Css* = SCE_CSS_*。

    /// <summary>HTML/XML 标签名（&lt;div&gt; 的 div）：亮色蓝、暗色品红，默认加粗</summary>
    private static DrawingColor HtmlTagC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x00, 0x00, 0xFF);      // 标签：蓝
    /// <summary>HTML/XML 属性名（class= 的 class）与 CSS 伪类：亮色暗红，暗色黄绿</summary>
    private static DrawingColor HtmlAttrC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0xFF, 0x00, 0x00);     // 属性：红
    /// <summary>HTML 属性值里的数字（width="100" 的 100）与内嵌 JS 的数字：亮色青，暗色紫</summary>
    private static DrawingColor HtmlNumC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x7F);      // 数字：青
    /// <summary>HTML 注释、CDATA、处理指令一类「原文透传」区块：亮色橄榄，暗色灰绿</summary>
    private static DrawingColor HtmlCommentC => IsDark ? D(0x75, 0x71, 0x5E) : D(0x80, 0x80, 0x00);  // 注释：橄榄
    /// <summary>HTML 实体引用（转义序列，如 &amp;lt; &#38;copy 这类 &amp; 开头的记号）与 SCE_H_OTHER：亮色紫，暗色紫，刻意与普通字符串区分</summary>
    private static DrawingColor HtmlEntityC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x80, 0x00, 0x80);   // 实体：紫
    /// <summary>属性值整体（SCE_H_VALUE）与 JSON 的 IRI 紧凑形式：亮色品红，暗色淡黄</summary>
    private static DrawingColor HtmlValueC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0xFF, 0x00, 0xFF);    // 属性值：品红
    /// <summary>HTML 内嵌 &lt;script&gt; 里的 JS 关键字（var/function/return…）：亮色紫蓝，暗色品红</summary>
    private static DrawingColor JsKeywordC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x80, 0x00, 0xFF);    // HTML 内嵌 JS 关键字
    /// <summary>JS 运算符/符号槽位（SCE_HJ_SYMBOLS）：刻意用接近正文的色，避免标点抢视觉</summary>
    private static DrawingColor JsSymC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0x00, 0x00, 0x00);
    /// <summary>JSON 属性名（"key": 的 key）：亮色紫，暗色黄绿，加粗后与字符串值一眼分得开</summary>
    private static DrawingColor JsonPropC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x88, 0x0A, 0xE8);      // JSON 属性名
    /// <summary>JSON 字符串值：亮色暗红（与属性名的紫配对），暗色淡黄</summary>
    private static DrawingColor JsonStrC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x00);       // JSON 字符串
    /// <summary>JSON 数字：与 HTML 数字同色系（青/紫），保持「数值一个色」的直觉</summary>
    private static DrawingColor JsonNumC => IsDark ? D(0xAE, 0x81, 0xFF) : D(0x00, 0x7F, 0x7F);       // JSON 数字
    /// <summary>JSON 字面量关键字 true/false/null：亮色薄荷绿，暗色亮青</summary>
    private static DrawingColor JsonKwC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x0B, 0xCE, 0xA7);        // JSON 关键字
    /// <summary>CSS 默认样式（也就是选择器整体，如 .a:hover、#b p）：亮色洋红，暗色近白</summary>
    private static DrawingColor CssDefaultC => IsDark ? D(0xF8, 0xF8, 0xF2) : D(0xFF, 0x00, 0x80);   // CSS 选择器
    /// <summary>CSS 元素选择器与 class 名（body、.panel）：亮色藏青，暗色黄绿</summary>
    private static DrawingColor CssTagC => IsDark ? D(0xA6, 0xE2, 0x2E) : D(0x00, 0x00, 0x7F);
    /// <summary>CSS1 属性名（color、margin）：亮色亮蓝，暗色亮青</summary>
    private static DrawingColor CssPropC => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0x40, 0xE0);      // CSS 属性
    /// <summary>CSS2/扩展属性与扩展标识符：比 CSS1 略浅一档（亮色亮蓝青），用来表达「方言属性」层级</summary>
    private static DrawingColor CssProp2C => IsDark ? D(0x66, 0xD9, 0xEF) : D(0x00, 0xA0, 0xE0);
    /// <summary>CSS 值（: 号右边的 10px、red、#fff）：亮色紫，暗色淡黄</summary>
    private static DrawingColor CssValueC => IsDark ? D(0xE6, 0xDB, 0x74) : D(0x7F, 0x00, 0x7F);     // CSS 值
    /// <summary>CSS 的 #id 选择器（注意别和十六进制颜色值混：词法器已把它们分到不同样式槽）</summary>
    private static DrawingColor CssIdC => IsDark ? D(0xFD, 0x97, 0x1F) : D(0x00, 0x7F, 0x7F);        // #id
    /// <summary>!important 声明：亮色橙、暗色橙黄，加粗，是整篇里最需要跳出来的记号</summary>
    private static DrawingColor ImportantC => IsDark ? D(0xFD, 0x97, 0x1F) : D(0xFF, 0x80, 0x00);    // !important
    /// <summary>@ 规则（@media/@import/@font-face 的关键字部分）：亮色橄榄，暗色品红</summary>
    private static DrawingColor AtRuleC => IsDark ? D(0xF9, 0x26, 0x72) : D(0x7F, 0x7F, 0x00);       // @规则

    /// <summary>
    /// 将主题应用到 Scintilla 编辑器（含语法高亮样式）
    /// </summary>
    /// <param name="sci">要上色的 Scintilla 控件实例（须已创建窗口句柄，否则属性写进去也会被丢）</param>
    /// <param name="lexerName">当前生效的词法器名（EditorControl.CurrentLexerName）；
    /// null/空串时退回读控件的 <c>LexerLanguage</c>，再读不到就按纯文本走通用样式表</param>
    /// <remarks>
    /// 每次调用都是「全量重建」而不是增量修补：先 StyleResetDefault 抹掉全部样式再逐槽刷色。
    /// 这样字体/字号改了、主题换了都只需要重跑一次，不用维护「哪些样式被谁改过」的状态。
    /// 调用方一定要在装好词法器之后再调它（见 EditorControl.ApplyLexer 的顺序），
    /// 否则会拿旧词法器名去分派语言样式，出现「有颜色但配错语言」。
    /// </remarks>
    public static void ApplyToScintilla(ScintillaNET.Scintilla sci, string? lexerName = null)
    {
        // ===== 第 0 步：清场并铺默认样式 =====
        // StyleResetDefault = SCI_STYLERESETDEFAULT，把「出厂默认样式」恢复成 Scintilla 内置值；
        // StyleClearAll   = SCI_STYLECLEARALL，用当前默认样式刷所有已用槽位（必须在设好字体后调用，
        // 否则各语言样式的字体/字号继承不到，会出现中文行高突变）
        sci.StyleResetDefault();
        var def = sci.Styles[ScintillaNET.Style.Default];
        // 字体取用户设置；空值兜底成等宽 Consolas，字号四舍五入成整数（Scintilla 只收整数磅值），
        // 非法/未初始化（<=0）时固定 10pt，避免样式表被写成 0 号字而整篇空白
        def.Font = string.IsNullOrWhiteSpace(SettingsService.Current.FontFamily) ? "Consolas" : SettingsService.Current.FontFamily;
        def.Size = SettingsService.Current.FontSize > 0 ? (int)Math.Round(SettingsService.Current.FontSize) : 10;
        // 文本区底色与正文色：底色跟 WPF 主背景同值，正文用标识符色（暗色近白/亮色近黑）
        def.BackColor = IsDark ? D(0x1E, 0x1E, 0x1E) : D(0xFF, 0xFF, 0xFF);
        def.ForeColor = IdentifierC;
        sci.StyleClearAll();

        // ===== 第 1 步：行号边距 =====
        // 0 号边距（行号）单独配色：底色比文本区略深一档，数字用中灰，
        // 让「边距」和「正文」在两种主题下都有明显分界又不刺眼
        var lineNumber = sci.Styles[ScintillaNET.Style.LineNumber];
        lineNumber.BackColor = IsDark ? D(0x25, 0x25, 0x26) : D(0xF0, 0xF0, 0xF0);
        lineNumber.ForeColor = IsDark ? D(0x85, 0x85, 0x85) : D(0x88, 0x88, 0x88);

        // ===== 第 2 步：当前行高亮 =====
        // CARETLINEVISIBLE + 底色：光标所在行整行铺一层极淡的色，取代参考实现的「行框」观感；
        // 暗色只比背景亮一点点（2A2D2E），亮色用极淡蓝（E8F2FF），避免与选区色混淆
        sci.CaretLineVisible = true;
        sci.CaretLineBackColor = IsDark ? D(0x2A, 0x2D, 0x2E) : D(0xE8, 0xF2, 0xFF);

        // ===== 第 3 步：选区 =====
        // SetSelectionBackColor(使用自定义色, 颜色)；亮/暗两值与 WpfSelection 严格对应。
        // 前景也一并指定：暗色下让选中的浅字变纯白，保证与深蓝底色仍有对比
        sci.SetSelectionBackColor(true, IsDark ? D(0x2D, 0x4D, 0x7E) : D(0xAD, 0xD6, 0xFF));
        sci.SetSelectionForeColor(true, IsDark ? D(0xFF, 0xFF, 0xFF) : D(0x00, 0x00, 0x00));

        // 光标竖线颜色：暗色用中灰（纯白太扎眼），亮色用黑
        sci.CaretForeColor = IsDark ? D(0xAE, 0xAF, 0xAD) : D(0x00, 0x00, 0x00);

        // ===== 括号配对高亮（完全对齐参考编辑器）=====
        // 参考实现只改括号字符的字色：配对成功变蓝、配不上变红，不画任何底色框。
        // 样式 34/35 在当前原生 Scintilla 5.3.6 实测不渲染，故用 TextFore 指示器改字色。
        // 8 号指示器已被查找高亮占用，这里用 9（配对）/10（配不上）。
        // Alpha=255 是必须的：indicator 默认半透明，不写满会把前景色混进底色里看不清。
        var braceInd = sci.Indicators[BraceIndicatorIndex];
        braceInd.Style = ScintillaNET.IndicatorStyle.TextFore;
        braceInd.Alpha = 255;
        braceInd.ForeColor = BraceMatchedFore;
        var braceBadInd = sci.Indicators[BraceBadForeIndicatorIndex];
        braceBadInd.Style = ScintillaNET.IndicatorStyle.TextFore;
        braceBadInd.Alpha = 255;
        braceBadInd.ForeColor = BraceBadFore;

        // ===== 折叠边距的 7 个 marker 槽位 =====
        // 25..31 正是 Scintilla 的 SC_MARKNUM_FOLDEREND..FOLDEROPEN（折叠树的连线与 +/- 方框），
        // 图标形状由 EditorControl 出厂时设好，这里只随主题刷两色。
        // 注意本处的分工是 SetForeColor 写「贴近文本区底色」的那档、SetBackColor 写灰色，
        // 而 EditorControl.ConfigureFoldMarker 出厂时是反过来的（浅线条色作 Fore、灰作 Back）；
        // 两处口径不一致，实际观感以后跑的这一次为准（主题刷新在出厂配置之后）。
        var markerColor = IsDark ? D(0xA0, 0xA0, 0xA0) : D(0x90, 0x90, 0x90);
        for (int i = 25; i <= 31; i++)
        {
            sci.Markers[i].SetForeColor(IsDark ? D(0x1E, 0x1E, 0x1E) : D(0xF5, 0xF5, 0xF5));
            sci.Markers[i].SetBackColor(markerColor);
        }

        // ===== 第 4 步：语言专用语法样式（最后做，会覆盖上面 ClearAll 铺的默认色）=====
        ApplySyntaxColors(sci, lexerName);
    }

    /// <summary>
    /// 语法样式分派入口：按词法器名选择对应的那张「样式槽位 → 语义色」表。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <param name="lexerName">词法器名（Lexilla 的 language 名，如 cpp/python/hypertext/json/css）；
    /// 传 null 或空串时改用控件当前 <c>LexerLanguage</c> 的读回值</param>
    /// <remarks>
    /// 名字一律转小写再比较：Lexilla 的名字表大小写混用（"HTML"/"hypertext"/"Xml" 都可能），
    /// 而这里的分支写的是规范小写名。
    /// 分支覆盖不到的词法器（ruby/perl/lua/asm/fortran/vb/ada/lisp 等）统一落到
    /// <see cref="ApplyGenericColors"/>——它们的槽位编号大致遵循「1/2 注释、3-4 数字、
    /// 5 关键字、6/7 字符串」的 Scintilla 惯例，所以通用表能给个八九不离十的效果。
    /// 注意 hypertext 与 xml 共用一张表（SCE_H_* 家族对两者兼容），
    /// cpp 一张表同时服务 C/C++/C#/Java/JS/TS/Go/Swift/Kotlin——这些语言在本工具里都借 cpp 词法器。
    /// </remarks>
    private static void ApplySyntaxColors(ScintillaNET.Scintilla sci, string? lexerName)
    {
        string language;
        if (!string.IsNullOrEmpty(lexerName))
        {
            // 调用方（EditorControl）已经知道真正生效的词法器名，优先信它
            language = lexerName.ToLowerInvariant();
        }
        else
        {
            // 兜底路径：句柄未就绪或控件不支持该属性时读回会抛异常，按「未知语言」处理而不是崩
            try { language = (sci.LexerLanguage ?? string.Empty).ToLowerInvariant(); }
            catch { language = string.Empty; }
        }

        switch (language)
        {
            // C 系一族：cpp 词法器 + 内嵌 JS 系（javascript/typescript 也在同一张 SCE_C_* 表上着色）
            // 说明：后两个 case 目前基本走不到——GetLexerName 把 .js/.ts 归到了 "cpp"，
            // 它们是为「将来接入独立 JS/TS 词法器」留的备档分支，行为与 cpp 完全一致。
            case "cpp":
            case "javascript":
            case "typescript":
                ApplyCppColors(sci); break;
            case "python": ApplyPythonColors(sci); break;
            // HTML 与 XML 共用 SCE_H_* 家族；php 也映射到 hypertext 所以自然走这张表
            case "hypertext":
            case "xml": ApplyHtmlColors(sci); break;
            case "json": ApplyJsonColors(sci); break;
            case "css": ApplyCssColors(sci); break;
            case "sql": ApplySqlColors(sci); break;
            case "markdown": ApplyMarkdownColors(sci); break;
            case "yaml": ApplyYamlColors(sci); break;
            case "bash": ApplyBashColors(sci); break;
            // batch 与 powershell 共用一张表（词法器同源：都是「行注释 + 命令词 + 标签」结构）
            case "batch":
            case "powershell": ApplyBatchColors(sci); break;
            // props 同时服务 .properties/.ini/.cfg/.env 和 TOML（键值 + [节] + #注释）
            case "props": ApplyPropsColors(sci); break;
            case "r": ApplyRColors(sci); break;
            case "diff": ApplyDiffColors(sci); break;
            default: ApplyGenericColors(sci); break;
        }
    }

    // SCE_C_* / C# / C++ / Java / TS / Kotlin / Swift
    /// <summary>
    /// 刷 C 系词法器的语法样式（样式槽位取自 Scintilla 的 SCE_C_* 常量表）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 这张表服务的语言最多：C/C++、C#、Java、JavaScript、TypeScript、Go、Swift、Kotlin、Arduino
    /// 都被 <see cref="LexerService.GetLexerName"/> 归到 "cpp" 词法器（Lexilla 这份构建里没有
    /// 对应的独立词法器，宁可词法器近似、再靠注入关键字表补足高亮，
    /// 也不能给个不存在的名字导致整篇无色）。Rust 有独立词法器，但分派表里没为它单开一行，
    /// 因此落在 <see cref="ApplyGenericColors"/> 上。
    /// 槽位语义与 ND2 的 QsciLexerCPP 一致：5 = 主关键字（对应注入的 SetKeywords(0)），
    /// 16 = 二级关键字（SetKeywords(1)，本工具用来放类型名），26 = TODO/FIXME 任务标记。
    /// 只刷前景（底色保持继承默认），这也是参考实现的样子——高亮不产生色块。
    /// </remarks>
    private static void ApplyCppColors(ScintillaNET.Scintilla sci)
    {
        // 注释三档：块注释 / 行注释 / 文档注释
        SetStyle(sci, 1, Comment);                  // 块注释
        SetStyle(sci, 2, Comment);                  // 行注释
        SetStyle(sci, 3, CommentDocC);              // 文档注释
        SetStyle(sci, 4, NumberC);                  // 数字
        // 关键字两档：主关键字加粗最重，二级关键字（类型名）用青色
        SetStyle(sci, 5, KeywordC, bold: true);     // 关键字
        // 字符串家族：双引号、单引号字符、UUID 三档都是字符串紫
        SetStyle(sci, 6, StringC);                  // 字符串
        SetStyle(sci, 7, StringC);                  // 字符
        SetStyle(sci, 8, StringC);                  // UUID
        // 预处理 / 运算符 / 标识符
        SetStyle(sci, 9, PreprocC);                 // 预处理器
        SetStyle(sci, 10, OperatorC);               // 操作符
        SetStyle(sci, 11, IdentifierC);             // 标识符
        // 异常与变体字符串：未闭合字符串按运算符色警示，逐字/原始/三引号串另用一色
        SetStyle(sci, 12, OperatorC);               // 未结束字符串
        SetStyle(sci, 13, VerbatimC);               // 逐字字符串
        SetStyle(sci, 14, RegexC);                  // 正则
        // 文档注释家族：行式文档注释、文档内关键字、写错的关键字
        SetStyle(sci, 15, CommentDocC);             // 文档行注释
        SetStyle(sci, 16, TypeC, bold: true);       // 二级关键字 / 类型名
        SetStyle(sci, 17, DocKeywordC);             // 文档关键字
        SetStyle(sci, 18, DocKeywordErrC);          // 文档关键字错误
        // 全局类名（词法器按 include 推导的已知类）
        SetStyle(sci, 19, TypeC);                   // 全局类
        // C++11/14 新增字符串形态：R"(...)" 原始串、Python 风格三引号、哈希引号串
        SetStyle(sci, 20, StringC);                 // 原始字符串
        SetStyle(sci, 21, VerbatimC);               // 三引号字符串
        SetStyle(sci, 22, VerbatimC);               // 哈希引号字符串
        // 26 = SCE_C_TASKMARKER：注释行里的 TODO/FIXME 单词
        SetStyle(sci, 26, TaskMarkerC, bold: true); // TODO 标记
    }

    // SCE_P_*
    /// <summary>
    /// 刷 Python 词法器的语法样式。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 槽位编号沿用参考实现（ND2 / QScintilla 的 QsciLexerPython）那张表，注释里的「NT」
    /// 是 Not Recognised（词法器没能确认这段结束）的缩写，例如三引号串没闭合时落到 13/14。
    /// 8 = 类名、9 = 函数名是 Python 特有的两档，这里分别给类型青与函数蓝，
    /// 让用户不装任何插件也能一眼看出「这是 class、这是 def」。
    /// </remarks>
    private static void ApplyPythonColors(ScintillaNET.Scintilla sci)
    {
        // 注释三档 + 数字 + 关键字
        SetStyle(sci, 1, Comment);                 // 行注释
        SetStyle(sci, 2, Comment);                 // 块注释
        SetStyle(sci, 3, Comment);                 // 注释 NT
        SetStyle(sci, 4, NumberC);                 // 数字
        SetStyle(sci, 5, KeywordC, bold: true);    // 关键字
        // 三种引号形态（三单引号、三双引号）都是多行原文串
        SetStyle(sci, 6, StringC);                 // 三引号字符串
        SetStyle(sci, 7, StringC);                 // 三双引号字符串
        // 定义体：类名加粗、函数/方法名单独一色
        SetStyle(sci, 8, TypeC, bold: true);       // 类名
        SetStyle(sci, 9, FuncC);                   // 函数名
        // 运算符与标识符
        SetStyle(sci, 10, OperatorC);              // 操作符
        SetStyle(sci, 11, IdentifierC);            // 标识符
        // NT（Not Recognised）三档：词法器没能收尾的块注释/三引号串，用普通注释色与字符串色兜住
        SetStyle(sci, 12, Comment);                // 块注释 NT
        SetStyle(sci, 13, StringC);                // 三引号 NT
        SetStyle(sci, 14, StringC);                // 三双引号 NT
        // @decorator 装饰器行
        SetStyle(sci, 16, FuncC);                  // 装饰器
    }

    // SCE_H_* （HTML / XML，含内嵌 JS）
    /// <summary>
    /// 刷 HTML / XML 词法器的语法样式（SCLEX_HTML 与 SCLEX_XML 共用 SCE_H_* 这一族槽位）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 服务的文件类型：.html/.htm/.shtml/.xhtml、.xml/.svg/.xaml/.resx/.csproj/.slnx，
    /// 以及被映射到 hypertext 的 .php。15/16 是 ASP/JSP 专用槽位，本工具不做区分所以跳过。
    /// 40..52 是「HTML 里内嵌 &lt;script&gt;」用的 JS 子样式表（SCE_HJ_*），
    /// 不刷的话脚本段会整块显示成默认色——参考实现的样式表同样是两段一起配的。
    /// </remarks>
    private static void ApplyHtmlColors(ScintillaNET.Scintilla sci)
    {
        // ===== 标签 / 属性 / 值 =====
        SetStyle(sci, 0, IdentifierC);              // 默认
        SetStyle(sci, 1, HtmlTagC, bold: true);     // 标签
        SetStyle(sci, 2, ErrorC);                   // 未知标签
        SetStyle(sci, 3, HtmlAttrC);                // 属性
        SetStyle(sci, 4, ErrorC);                   // 未知属性
        SetStyle(sci, 5, HtmlNumC);                 // 数字
        // 引号包起来的属性值：双引号与单引号两档，颜色一致，只是词法器内部分类不同
        SetStyle(sci, 6, StringC);                  // 双引号字符串
        SetStyle(sci, 7, StringC);                  // 单引号字符串
        // 标签间文本、实体、结束标签
        SetStyle(sci, 8, HtmlEntityC);              // 其他
        SetStyle(sci, 9, HtmlCommentC);             // 注释
        SetStyle(sci, 10, HtmlEntityC);             // 实体
        SetStyle(sci, 11, HtmlTagC);                // 标签结束
        // XML 声明与处理指令：&lt;?xml ...?&gt; 的头尾两段
        SetStyle(sci, 12, HtmlTagC, bold: true);    // XML 开始
        SetStyle(sci, 13, HtmlTagC, bold: true);    // XML 结束
        SetStyle(sci, 14, HtmlTagC);                // 脚本
        // CDATA 段、处理指令、属性值整体、扩展注释（SGML/XML 特有区块）
        SetStyle(sci, 17, HtmlCommentC);            // CDATA
        SetStyle(sci, 18, HtmlCommentC);            // 处理指令
        SetStyle(sci, 19, HtmlValueC);              // 属性值
        SetStyle(sci, 20, HtmlCommentC);            // 扩展注释

        // 内嵌 JavaScript（SCE_HJ_* = 40..52）
        // 40 号是「脚本区起始」标记，不上色；41 起才是真正的 JS 样式，语义与 C 表一一对应
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
    /// <summary>
    /// 刷 JSON 词法器的语法样式（SCLEX_JSON，服务 .json 文件）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 词法器默认只认严格 JSON；6/7 两档是注释槽位，来自「允许注释」的方言配置，
    /// 这里照参考实现一并上色，这样带注释的 jsonc 也不是整片灰。
    /// 9/10 是 IRI（国际化 URI）相关槽位，12/13 属于 JSON-LD 方言的错误提示，
    /// 日常文件几乎碰不到，配色只求「出现了也不刺眼」。
    /// </remarks>
    private static void ApplyJsonColors(ScintillaNET.Scintilla sci)
    {
        // 基础三件套：默认文本、数字、字符串
        SetStyle(sci, 0, IdentifierC);              // 默认
        SetStyle(sci, 1, JsonNumC);                 // 数字
        SetStyle(sci, 2, JsonStrC);                 // 字符串
        // 语法错误：字符串跨行没闭合
        SetStyle(sci, 3, ErrorC);                   // 未结束字符串
        // 属性名是 JSON 的主角，加粗 + 专用紫，和字符串值（红）拉开
        SetStyle(sci, 4, JsonPropC, bold: true);    // 属性名
        SetStyle(sci, 5, PreprocC);                 // 转义序列
        // 注释（严格 JSON 没有，jsonc/带注释配置里有）
        SetStyle(sci, 6, Comment);                  // 行注释
        SetStyle(sci, 7, Comment);                  // 块注释
        SetStyle(sci, 8, OperatorC);                // 操作符
        // IRI 两档：普通形式与紧凑（不带引号）形式
        SetStyle(sci, 9, HtmlTagC);                 // IRI
        SetStyle(sci, 10, HtmlValueC);              // IRI 紧凑
        // 字面量关键字 true/false/null
        SetStyle(sci, 11, JsonKwC, bold: true);     // 关键字 true/false/null
        // JSON-LD 方言的关键字与错误槽位
        SetStyle(sci, 12, ErrorC);                  // LD 关键字
        SetStyle(sci, 13, ErrorC);                  // 错误
    }

    // SCE_CSS_*
    /// <summary>
    /// 刷 CSS 词法器的语法样式（SCLEX_CSS，服务 .css/.scss/.less）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 这张表同时覆盖现代 Scintilla 新增的两档方言槽位：14 = CSS2/扩展属性、15/16 = 扩展伪类与
    /// 扩展标识符，用于把「厂商前缀 / 后来才标准化的属性」和普通属性区分开。
    /// 注意 0 号（default）在 CSS 词法器里承担的是「选择器文本」而不是空白正文，所以给它洋红色，
    /// 这是这套配色里唯一「默认样式反而更抢眼」的语言。
    /// </remarks>
    private static void ApplyCssColors(ScintillaNET.Scintilla sci)
    {
        // 选择器一族：默认（选择器整体）、元素名、class、伪类，以及词法器不认识的伪类
        SetStyle(sci, 0, CssDefaultC);              // 默认（选择器）
        SetStyle(sci, 1, CssTagC, bold: true);      // 标签
        SetStyle(sci, 2, CssTagC);                  // class
        SetStyle(sci, 3, HtmlAttrC);                // 伪类
        SetStyle(sci, 4, ErrorC);                   // 未知伪类
        SetStyle(sci, 5, OperatorC);                // 操作符
        // 属性名两代：CSS1（6）与 CSS2/扩展（14），未知属性（7）涂红提示拼写错误
        SetStyle(sci, 6, CssPropC);                 // CSS1 属性
        SetStyle(sci, 7, ErrorC);                   // 未知属性
        // 值、注释、id 选择器
        SetStyle(sci, 8, CssValueC);                // 值
        SetStyle(sci, 9, Comment);                  // 注释
        SetStyle(sci, 10, CssIdC);                  // #id
        // 声明块之外的记号：!important、@规则、@media
        SetStyle(sci, 11, ImportantC, bold: true);  // !important
        SetStyle(sci, 12, AtRuleC, bold: true);     // @规则
        SetStyle(sci, 13, AtRuleC);                 // @media
        // 方言扩展三档：CSS2 属性、扩展伪类、扩展标识符
        SetStyle(sci, 14, CssProp2C);               // CSS2 属性
        SetStyle(sci, 15, HtmlAttrC);               // 扩展伪类
        SetStyle(sci, 16, CssProp2C);               // 扩展标识符
    }

    // SCE_SQL_*
    /// <summary>
    /// 刷 SQL 词法器的语法样式（SCLEX_SQL，服务 .sql）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// SQL 词法器的关键字分方言配成多张表（主表之外还有各家数据库的方言表），
    /// 这里只给「主表命中」用的 5 号样式槽上色，其余方言槽沿用默认色，
    /// 避免把某家数据库的私有函数涂成关键字色。注释三档（1 块 / 2 行 / 3 文档）与 C 系同号不是
    /// 巧合——两者词法器同源。10 号往后是标识符与内置函数一族的槽位，本表只按参考实现刷了一档。
    /// </remarks>
    private static void ApplySqlColors(ScintillaNET.Scintilla sci)
    {
        // 注释三档
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, Comment);                 // 行注释
        SetStyle(sci, 3, Comment);                 // 文档注释
        // 数值与关键字（SELECT/FROM/WHERE 等主表）
        SetStyle(sci, 4, NumberC);                 // 数字
        SetStyle(sci, 5, KeywordC, bold: true);    // 关键字
        // 字符串与字符常量：SQL 的 'x' 既是字符串也是字符
        SetStyle(sci, 6, StringC);                 // 字符串
        SetStyle(sci, 7, StringC);                 // 字符
        // 标识符（表名/列名）显式刷成正文色：不给它特殊待遇，保证与默认文本观感一致
        SetStyle(sci, 13, IdentifierC);            // 标识符
    }

    // SCE_MARKDOWN_*
    /// <summary>
    /// 刷 Markdown 词法器的语法样式（SCLEX_MARKDOWN，服务 .md/.markdown）：
    /// 标题、强调（粗/斜）、代码、列表、引用、删除线、分隔线、链接各自一档。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 3 号起连续 7 档（3..9）都按「标题」刷同色加粗——编号沿用参考实现（ND2 / QScintilla）那张表，
    /// 表达层级只靠粗体，本工具不做「标题字号渐变」。若底层新版 Scintilla 已把这些槽位挪给他用
    /// （Markdown 词法器改版过几次），这个循环会把挪走的那档也一并刷成标题粗体，
    /// 属于「照抄参考编号」的已知代价，实测对观感影响很小。
    /// 粗体/斜体各有两档（10/11、12/13）是因为词法器区分「星号写法」和「下划线写法」，
    /// 上色时无需区分，成对配置即可。22 号链接加下划线，与 URL 热点（indicator）互不影响。
    /// </remarks>
    private static void ApplyMarkdownColors(ScintillaNET.Scintilla sci)
    {
        // 各级标题：一个循环刷完，颜色与粗细都相同
        for (int i = 3; i <= 9; i++) SetStyle(sci, i, TagC, bold: true);  // 各级标题
        // 强调：粗体两档、斜体两档（星号写法与下划线写法各一）
        SetStyle(sci, 10, TypeC, bold: true);      // 粗体
        SetStyle(sci, 11, TypeC, bold: true);
        SetStyle(sci, 12, TypeC, italic: true);    // 斜体
        SetStyle(sci, 13, TypeC, italic: true);
        // 代码：行内反引号、缩进块、围栏块三档，都归字符串紫
        for (int i = 14; i <= 16; i++) SetStyle(sci, i, StringC);         // 代码
        // 列表与引用
        SetStyle(sci, 17, KeywordC);               // 无序列表
        SetStyle(sci, 18, KeywordC);               // 有序列表
        SetStyle(sci, 19, Comment);                // 引用
        // 其他行内记号：删除线、分隔线、链接
        SetStyle(sci, 20, StringC);                // 删除线
        SetStyle(sci, 21, PreprocC);               // 分隔线
        SetStyle(sci, 22, TagC, underline: true);  // 链接
    }

    // SCE_YAML_*
    /// <summary>
    /// 刷 YAML 词法器的语法样式（SCLEX_YAML，服务 .yaml/.yml）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// YAML 的槽位命名和别家差得最远：6 号「文档分隔符」指 <c>---</c> / <c>...</c> 行，
    /// 5 号「引用」指 &amp;anchor 与 *alias，都不是字面意义的关键字/字符串。
    /// 键名（如 name: 的 name）在词法器里就是 2 号 identifier 槽，所以它拿标识符色而不是特殊色，
    /// 与字符串值（7 号 text）的区分靠紫/黑对比。
    /// </remarks>
    private static void ApplyYamlColors(ScintillaNET.Scintilla sci)
    {
        // 注释与键名、值关键字（true/false/null/yes/no 等）
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, IdentifierC);             // 标识符
        SetStyle(sci, 3, KeywordC);                // 关键字
        SetStyle(sci, 4, NumberC);                 // 数字
        // 锚点/别名引用与文档分隔线
        SetStyle(sci, 5, FuncC);                   // 引用
        SetStyle(sci, 6, PreprocC, bold: true);    // 文档分隔符
        // 普通标量文本与错误行
        SetStyle(sci, 7, StringC);                 // 文本
        SetStyle(sci, 8, ErrorC);                  // 错误
        // 结构符号（: | > - 等）与日期标量（YAML 会单独识别 ISO 日期）
        SetStyle(sci, 9, OperatorC);               // 操作符
        SetStyle(sci, 10, TypeC);                  // 日期
    }

    // SCE_SH_*
    /// <summary>
    /// 刷 Bash / Shell 词法器的语法样式（SCLEX_BASH，服务 .sh/.bash/.zsh/.bashrc 与 Dockerfile）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// Dockerfile 也走这张表：<see cref="LexerService.GetLexerName"/> 把它归到 bash 词法器
    /// （这份 Lexilla 构建里没有 dockerfile 专用词法器），指令名至少能落在 bash 的
    /// 「词 / 标识符」槽位上，比整篇纯文本好看。
    /// 词法器的关键字表可以配多份（heredoc/内嵌脚本等），这里只按语义刷颜色。
    /// 注：9 号（变量标量 $var）在参考实现里没有单独配色，所以这里跳过它。
    /// </remarks>
    private static void ApplyBashColors(ScintillaNET.Scintilla sci)
    {
        // 词法器自己判定「这条命令不在 PATH 里」之类的错误会涂 1 号
        SetStyle(sci, 1, ErrorC);                  // 错误
        SetStyle(sci, 2, Comment);                 // 注释
        SetStyle(sci, 3, NumberC);                 // 数字
        // 保留字（if/then/for/do…）与字符串两档（双引号、单引号）
        SetStyle(sci, 4, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 5, StringC);                 // 字符串
        SetStyle(sci, 6, StringC);                 // 字符
        SetStyle(sci, 7, OperatorC);               // 操作符
        // 普通命令名（作为标识符出现时）
        SetStyle(sci, 8, IdentifierC);             // 标识符
        // 变量展开：$1/$@ 之类参数与 `反引号命令替换`，都用函数色突出「这里是取值/执行」
        SetStyle(sci, 10, FuncC);                  // 参数 / 变量
        SetStyle(sci, 11, FuncC);                  // 反引号
    }

    // SCE_BAT_*
    /// <summary>
    /// 刷 Batch（.bat/.cmd）词法器的语法样式，也被 powershell 分支复用。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// Batch 词法器刻意只给四个槽位上色：注释（REM/::）、关键字表命中的词、外部命令/内建命令、
    /// 引号字符串。剩下的槽位（标签、回显、跳转）在参考实现里就是不着色的，这里保持一致——
    /// 批处理脚本体量小，全部上色反而难读。
    /// 注意 powershell 分支复用这张表：ND2 对 powershell 根本没建词法器（详见
    /// <see cref="LexerService.GetCommentSymbols"/> 的注释），实际运行时若装不上 "powershell"
    /// 会退回 "null" 走 <see cref="ApplyGenericColors"/>，这条分支基本是备档。
    /// </remarks>
    private static void ApplyBatchColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, KeywordC, bold: true);    // 词
        SetStyle(sci, 5, FuncC);                   // 命令
        SetStyle(sci, 8, StringC);                 // 字符串
    }

    // SCE_R_*
    /// <summary>
    /// 刷 R 语言词法器的语法样式（SCLEX_R，服务 .r/.rmd）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// R 的关键字分三档：2 = 语言保留字（if/for/function）、3 = 基础包关键字（TRUE/NA/NULL 等）、
    /// 4 = 其他关键字，本工具给 2 加粗、3 不加粗、4 用类型色，读起来层级清楚。
    /// 10/11 是 R 特有的中缀运算符（<c>%in%</c>、<c>%*%</c>）与「没闭合的中缀」，
    /// 前者按函数色、后者按错误色处理。
    /// </remarks>
    private static void ApplyRColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        // 三档关键字表：保留字加粗，基础常量不加粗，其它归类型色
        SetStyle(sci, 2, KeywordC, bold: true);    // 关键字
        SetStyle(sci, 3, KeywordC);                // 基础关键字
        SetStyle(sci, 4, TypeC);                   // 其它关键字
        SetStyle(sci, 5, NumberC);                 // 数字
        // 字符串两档（单引号/双引号在 R 里等价，词法器仍分开报）
        SetStyle(sci, 6, StringC);                 // 字符串
        SetStyle(sci, 7, StringC);                 // 字符串2
        SetStyle(sci, 8, OperatorC);               // 操作符
        // R 的中缀运算符族
        SetStyle(sci, 10, FuncC);                  // 中缀运算符 %in% 等
        SetStyle(sci, 11, ErrorC);                 // 未闭合中缀
    }

    // SCE_PROPS_*
    /// <summary>
    /// 刷 Properties/INI 词法器的语法样式（SCLEX_PROPERTIES）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 这张表面前服务的文件类型最多：.properties/.ini/.cfg/.conf/.env/.gitignore/.editorconfig
    /// 以及 .toml（TOML 的「键值 + [表名] + # 注释」与 props 同构，所以借词法器）。
    /// 该词法器本身只有 0..4 五个槽位（默认/注释/节/赋值符/默认值），
    /// 这里额外刷的 5 号是按参考实现的习惯当「键名」用，实际可能落在未定义槽位上。
    /// </remarks>
    private static void ApplyPropsColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 2, TagC, bold: true);        // 节
        SetStyle(sci, 3, OperatorC);               // 赋值符
        SetStyle(sci, 5, TypeC);                   // 键
    }

    // SCE_DIFF_*
    /// <summary>
    /// 刷 Diff/Patch 词法器的语法样式（SCLEX_DIFF，服务 .diff/.patch）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// 差异视图的行底色是另一回事（由 EditorControl.LineOps 用 marker 铺），
    /// 这里只负责 diff 文本本身的字色：文件头、删除行、新增行按参考实现刷成
    /// 标签蓝/错误红/数字橙，一眼能分辨 +/- 两类行。
    /// </remarks>
    private static void ApplyDiffColors(ScintillaNET.Scintilla sci)
    {
        SetStyle(sci, 1, Comment);                 // 注释
        SetStyle(sci, 3, TagC, bold: true);        // 文件头
        SetStyle(sci, 5, ErrorC);                  // 删除行
        SetStyle(sci, 6, NumberC);                 // 新增行
    }

    // 通用：适用于大多数简单 lexer
    /// <summary>
    /// 通用兜底样式表：未被专门覆盖的词法器（ruby/perl/lua/asm/fortran/pascal/vb/ada/cmake/
    /// makefile/verilog/vhdl/matlab/lisp 等，以及纯文本 "null"）都用它。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <remarks>
    /// Scintilla 大部分词法器的槽位排布有共同惯例：1/2 是注释、3 或 4 是数字、
    /// 4/5 是关键字、6/7 是字符串、9 是运算符、11 是标识符，所以一张表能凑合服务所有语言。
    /// 这里不加尾注是因为具体语义随词法器而变，写死了反而会误导。
    /// </remarks>
    private static void ApplyGenericColors(ScintillaNET.Scintilla sci)
    {
        // 注释两档（行/块）
        SetStyle(sci, 1, Comment);
        SetStyle(sci, 2, Comment);
        // 数值与关键字两档（不同词法器把数字放 3 或 4，都刷上）
        SetStyle(sci, 3, NumberC);
        SetStyle(sci, 4, KeywordC, bold: true);
        SetStyle(sci, 5, KeywordC, bold: true);
        // 字符串两档
        SetStyle(sci, 6, StringC);
        SetStyle(sci, 7, StringC);
        // 预处理/运算符/标识符
        SetStyle(sci, 9, PreprocC);
        SetStyle(sci, 10, OperatorC);
        SetStyle(sci, 11, IdentifierC);
    }

    /// <summary>
    /// 写一个语法样式：只改前景色和字形，不改底色（保持继承默认背景，避免出现色块）。
    /// </summary>
    /// <param name="sci">目标 Scintilla 控件</param>
    /// <param name="index">样式槽位号，语义由各 Apply*Colors 的注释标明（对应 SCE_* 常量）</param>
    /// <param name="fore">前景色（GDI+ Color，alpha 固定 255）</param>
    /// <param name="bold">是否加粗，默认 false</param>
    /// <param name="italic">是否斜体，默认 false</param>
    /// <param name="underline">是否下划线，默认 false</param>
    /// <remarks>三个字形开关都只在传 true 时才写：Scintilla 的样式属性是逐条生效的，
    /// 显式写 false 会覆盖词法器/其他样式表已经设好的字形。</remarks>
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
    // 名义上归在「主题服务」里，实际是给编辑器设置码页用的工具方法：
    // Scintilla 的 SCI_SETCODEPAGE 只认一个整数，而 .NET 侧拿到的是 Encoding 对象，
    // 两者之间需要一层翻译（UTF-8/UTF-16 在 .NET 里的 CodePage 与 Windows 码页号并不对应）。
    // 说明：当前工程的码页一律走 EncodingService 的整数码页常量，本方法暂无调用点，
    // 留着作为「拿到 Encoding 对象时」的统一换算出口。

    /// <summary>
    /// 把 .NET 的 <see cref="System.Text.Encoding"/> 翻译成 Scintilla 需要的码页号。
    /// </summary>
    /// <param name="encoding">当前文档使用的编码对象（来自 <c>EncodingService</c> 的检测结果）</param>
    /// <returns>
    /// UTF-8 返回 <c>SC_CP_UTF8</c> 约定的 65001（.NET 自己报的是 65008，Scintilla 不认）；
    /// UTF-16 LE 返回 1200、UTF-16 BE 返回 1201（供调用侧辨识，Scintilla 本身只支持 0=ANSI 与 65001）；
    /// 其余（GBK/Big5/Shift-JIS/latin1…）直接用 .NET 的 Windows 码页号，两者语义一致。
    /// </returns>
    /// <remarks>
    /// 这里刻意用引用相等（==）而不是 <c>Encoding.Equals</c>：Encoding.UTF8/Unicode/BigEndianUnicode
    /// 是框架给出的同一批静态实例，比对引用最快也最不容易被 codepage/编码器前缀差异影响。
    /// </remarks>
    public static int GetCodePage(System.Text.Encoding encoding)
    {
        // UTF-8：Scintilla 用 65001 表示「内部按 UTF-8 处理」，必须显式翻译
        if (encoding is System.Text.UTF8Encoding)
            return 65001;
        // UTF-16 两档：先于普通码页返回，避免落到 CodePage 上给出 Scintilla 不认的值
        if (encoding == System.Text.Encoding.Unicode)
            return 1200;
        if (encoding == System.Text.Encoding.BigEndianUnicode)
            return 1201;
        // 单字节/DBCS 代码页：.NET 的 CodePage 就是 Windows ANSI 码页号，可直接下发
        return encoding.CodePage;
    }
}
