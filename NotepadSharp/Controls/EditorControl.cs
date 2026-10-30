using System.Windows.Forms;
using System.Windows.Forms.Integration;
using DrawingColor = System.Drawing.Color;
using NotepadSharp.Services;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// 编辑器控件：用 WPF WindowsFormsHost 包裹 Scintilla 控件
/// </summary>
/// <remarks>
/// 本类是「一个文档 = 一个编辑器实例」的载体：Scintilla 是 WinForms 控件，装进
/// <see cref="WindowsFormsHost"/> 后才能落进 WPF 可视化树（ND2 那边对应
/// ScintillaEditView，一个文档一个实例）。
/// <para>
/// 位置口径（全仓库最容易踩的坑）：本文件里读写的几乎全是 Scintilla.NET 的**托管** API
/// ——它们对外是 UTF-16 码元位（托管层内部再换算成 Scintilla 文档的 UTF-8 字节偏移）。
/// 只有 <c>EditorControl.UrlHotspot.cs</c> 那一档刻意绕过托管层直发原生消息，用的才是
/// UTF-8 字节位。两边不要混用：中文一行里「字符数」和「字节数」差一倍以上，
/// 错位就从「起点 + 字符串长度」这种看似等价的写法开始。
/// </para>
/// 本文件只放核心封装（构造/边距/词法器/光标位置/书签/查找替换/显示选项/高亮标记），
/// 行操作、注释、URL 热点分别在同名的 partial 文件里。
/// </remarks>
public partial class EditorControl : WindowsFormsHost
{
    /// <summary>
    /// 真正干活的 Scintilla 子控件（WinForms 控件，一个实例只服务一个文档）。
    /// 构造函数里建好后交给 <see cref="WindowsFormsHost.Child"/>，之后所有编辑动作
    /// 都通过它下发 SCI_ 消息；外部要拿原生句柄或自定义消息，走 <see cref="Scintilla"/> 属性。
    /// </summary>
    private readonly Scintilla _scintilla;

    /// <summary>编辑区收到鼠标按下（每个编辑器实例共用，主窗口用它把程序抬到所有窗口最前）</summary>
    /// <remarks>
    /// 声明为 static 是刻意的：编辑区是 Win32 子 HWND，点击不进 WPF 输入管线，
    /// 主窗口只能靠这一个全局通知补一次「抬到最前」。因此不区分是哪个编辑器被按下，
    /// 订阅方（主窗口）只关心「有人在编辑区点了一下」这件事本身。
    /// </remarks>
    public static event Action? EditorMouseDown;

    // 书签使用的 marker 索引
    /// <summary>
    /// 书签占用的 marker 编号（Scintilla 每行 32 个 marker 槽位 0..31 里挑 2 号，
    /// 走 SCI_MARKERADD / SCI_MARKERDELETE / SCI_MARKERGET）。
    /// 0-19 段被折叠标记与差异标记分掉了，改这个编号要同步改边距掩码与
    /// <c>EditorControl.LineOps.cs</c> 里的槽位表。
    /// </summary>
    public const int BookmarkMarkerIndex = 2;
    /// <summary>2 号槽位对应的位掩码（1u &lt;&lt; 2）：SCI_MARKERGET 返回的是「本行所有 marker」的按位或，判断有无书签要跟它取与</summary>
    private const uint BookmarkMarkerMask = 1u << BookmarkMarkerIndex;

    // 查找高亮使用的 indicator 索引
    /// <summary>
    /// 「查找全部高亮」使用的 indicator 编号。
    /// ND2 的 URL 热点用的是 URL_INDIC = 8（scintillaeditview.h:97），本项目把 8 号让给查找高亮，
    /// URL 热点另找编号（见 <c>EditorControl.UrlHotspot.cs</c>）；9/10 号被括号配对占用。
    /// </summary>
    private const int FindIndicatorIndex = 8;

    /// <summary>正则零长匹配时显示在原地的气泡文字（ND2 的 "find-regex-zero-length-match"）</summary>
    private const string ZeroLengthMatchTip = "正则查找零长匹配";

    // 标记色使用的 indicator 起始索引与数量，编号沿用 ND2 的 SCE_UNIVERSAL_FOUND_STYLE_EXT1..EXT5
    /// <summary>标记色第一个槽位（1 号）对应的 indicator 编号，槽位 n 用 20+n，即 21..25；21 往上不与查找高亮/括号指示器冲突</summary>
    private const int MarkIndicatorBase = 21;
    /// <summary>标记色槽位总数（ND2 的 EXT1..EXT5 一共五档）</summary>
    public const int MarkColorCount = 5;

    /// <summary>5 种标记色的默认值（0xRRGGBB），下标 i 对应 indicator 21+i，即 ND2 的 mark_style_(i+1)</summary>
    /// <remarks>
    /// 数组本身是可变的静态字段：主题切换时由 <c>ThemeService</c> 覆盖默认色，
    /// 新开的编辑器实例装 indicator 时读的就是这里的当前值。
    /// </remarks>
    public static readonly int[] MarkColors = { 0x00FFFF, 0xFFFF00, 0x0080FF, 0xFF0080, 0xFF8000 };

    /// <summary>一条标记记录（对应 ND2 每个编辑器上的 m_curMarkList），F8 靠它判断要不要取消</summary>
    private sealed class MarkRecord
    {
        /// <summary>被标记的词面文本（记录时用的是查找框里的原文，不做扩展转义还原）；取消标记按它做 Ordinal 相等匹配</summary>
        public required string Text { get; init; }
        /// <summary>用的哪一档标记色（1..5），实际 indicator 编号 = <see cref="MarkIndicatorBase"/> - 1 + Slot；F8「同色再按一次取消、异色换色重标」就看它</summary>
        public int Slot { get; set; }
    }

    /// <summary>
    /// 本编辑器已打下的标记记录（每个实例一份，等价 ND2 的 m_curMarkList）。
    /// 只记「哪个词、用了第几档色」，不记具体位置：取消时按同一个查找条件重新扫一遍区间再清，
    /// 这样文档被编辑过之后也不会残留脏位置。
    /// </summary>
    private readonly List<MarkRecord> _markRecords = new();

    // 位置回退 / 前进双栈，照 ND2 的 m_prePosRecord / m_nextPosRecord：只有跨行才入栈，
    // 上限 MAX_PRE_NEXT_TIMES=30，满了丢最旧的一条；仅内存，不落配置。
    /// <summary>单个栈的最大深度（ND2 的 MAX_PRE_NEXT_TIMES），超了就丢最旧一条，防止长时间编辑把位置一路堆下去</summary>
    private const int MaxPreNextTimes = 30;
    /// <summary>回退栈（ND2 m_prePosRecord）：栈顶是「当前站的位置」，所以要留 1 条不弹；元素是 UTF-16 码元位</summary>
    private readonly List<int> _prePosRecord = new();
    /// <summary>前进栈（ND2 m_nextPosRecord）：只在头部插入（栈式），取用时也从头部取；跨行回退时把弹出来的位置塞这里</summary>
    private readonly List<int> _nextPosRecord = new();
    /// <summary>最后一次记录在案的光标位置，用来去重：同一次移动可能被 UpdateUI 回调好几次，只该入栈一次</summary>
    private int _curPos = 0;                    // ND2 构造函数里 m_curPos(0)，所以文档刚建好时光标在 0 不会压栈
    /// <summary>上一次看到的光标行号，ND2 只在「行号也变了」时才记位置，所以同一行内横向移动不会入栈</summary>
    private int _lastTrackedLine = -1;          // 对齐 QsciScintilla 的 oldLine(-1)

    /// <summary>
    /// 当前语言的注释符号，跟着词法器一起下发（ND2 也是把这三个值挂在词法器实例上）。
    /// 初值取「纯文本」那一档：ND2 新建文档挂的就是 QsciLexerText，行注释符是 #。
    /// 全空 = 这门语言没有注释动作，<see cref="CommentOps"/> 会直接不动作。
    /// </summary>
    public LexerService.CommentSymbols CommentSymbols { get; private set; } =
        LexerService.GetCommentSymbols("null", null);

    // Scintilla 标准折叠标记索引
    /// <summary>折叠标记「折叠结束（可展开）」槽位，SCI_FOLDMARKNUMS 约定的 25 号；折叠行尾显示的那个 + 号</summary>
    private const int SC_MARKNUM_FOLDEREND = 25;
    /// <summary>折叠标记「中途展开（上面有父层折叠）」槽位 26 号，画 - 号</summary>
    private const int SC_MARKNUM_FOLDEROPENMID = 26;
    /// <summary>折叠标记「中间层的竖线尾巴」槽位 27 号，画 T 角</summary>
    private const int SC_MARKNUM_FOLDERMIDTAIL = 27;
    /// <summary>折叠标记「最内层结尾的折角」槽位 28 号，画 L 角</summary>
    private const int SC_MARKNUM_FOLDERTAIL = 28;
    /// <summary>折叠标记「被折叠区内的竖线」槽位 29 号，画竖线</summary>
    private const int SC_MARKNUM_FOLDERSUB = 29;
    /// <summary>折叠标记「完全展开的父行」槽位 30 号，画 + 号</summary>
    private const int SC_MARKNUM_FOLDER = 30;
    /// <summary>折叠标记「已展开的父行」槽位 31 号，画 - 号</summary>
    private const int SC_MARKNUM_FOLDEROPEN = 31;
    /// <summary>折叠标记位掩码（25..31 这 7 个槽位）：SCI_SETMARGINMASKN 给 1 号边距设它，边距才只画折叠标记</summary>
    private const uint SC_MASK_FOLDERS = 0xFE000000u;

    /// <summary>
    /// 建一个编辑器实例：造 Scintilla 子控件、做一次性出厂设置、挂进 <see cref="WindowsFormsHost"/>。
    /// 调用方（主窗口/文档标签）不需要再做任何初始化。
    /// </summary>
    public EditorControl()
    {
        // Scintilla 子控件本身：Dock=Fill 让它跟着宿主客户区伸缩；这里的 Font 只影响 WinForms
        // 侧的默认字体，真正的等宽字体还要靠样式表（见 SetFont / ThemeService）
        _scintilla = new Scintilla
        {
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 10),
        };

        // Scintilla 的句柄创建后才真正能接收样式/词法器设置，否则在控件加入可视树之前调用会被丢弃。
        // 订阅必须早于 SetupEditor：里面的样式调用可能就地触发句柄创建，晚了这个事件就再也收不到。
        _scintilla.HandleCreated += OnScintillaHandleCreated;

        SetupEditor();
        // Child 是 WindowsFormsHost 的唯一子项入口；赋值后 WinForms 侧才会走创建句柄的流程
        Child = _scintilla;

        // 句柄也可能在构造/对象初始化阶段就被样式类属性就地创建（那时事件还没订阅上），
        // 这里补一次同样的初始化，避免词法器、主题、拖放接收器统统漏掉
        if (_scintilla.IsHandleCreated) OnScintillaHandleCreated(_scintilla, EventArgs.Empty);
    }

    /// <summary>
    /// 句柄就绪后的补初始化：词法器、主题、外部文件拖放接收器都必须在这一刻之后才有效。
    /// 三件事各管各的，任何一个失败不影响另外两个（ND2 同样是在视图建好之后才 setLexer / setStyles）。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用，句柄以 <see cref="_scintilla"/> 为准）</param>
    /// <param name="e">空事件参数（未使用）</param>
    private void OnScintillaHandleCreated(object? sender, EventArgs e)
    {
        ApplyLexer();
        ApplyTheme();
        AttachFileDropSink();
    }

    /// <summary>
    /// 出厂设置：换行符/Tab、三个边距、折叠与书签 marker、查找与标记 indicator、右键菜单、
    /// 以及全部事件订阅。只在构造函数里调用一次，之后由各个 Apply* 方法按需局部覆盖。
    /// </summary>
    private void SetupEditor()
    {
        // ===== 基本设置 =====
        // ND2 出厂是 WrapNone + 缩进参考线关（键 warp / indent 默认都 0），勾不勾都由视图菜单与配置决定
        _scintilla.WrapMode = ScintillaNET.WrapMode.None;
        _scintilla.IndentationGuides = IndentView.None;
        // ND2 在 ScintillaEditView 构造里按平台钉死换行符格式（scintillaeditview.cpp:1139-1145），
        // 紧跟着就是 setAutoIndent(true)；不设的话回车会插入单个 CR，和 ND2 不一致
        // EolMode 走 SCI_SETEOLMODE：只管「回车插入哪种行尾符」，不影响读取已有文件的行尾
        _scintilla.EolMode = Eol.CrLf;
        // Tab 宽度/空格缩进来自全局配置，配置变更时主窗口会对每个编辑器重调一次 ApplyTabSettings
        ApplyTabSettings();
        // 光标宽度 2 像素（SCI_SETCARETWIDTH），1 像素在高分屏上太细，跟着 ND2 的观感
        _scintilla.CaretWidth = 2;

        // ===== 边距设置 =====
        // 0: 行号
        // MarginType.Number = SCI_SETMARGINTYPEN(0, SC_MARGIN_NUMBER)，行号列；
        // Width 是像素宽度（不是字符数），50 够放 5~6 位行号
        _scintilla.Margins[0].Type = MarginType.Number;
        _scintilla.Margins[0].Width = 50;
        // 行号列不响应点击（SCI_SETMARGINSENSITIVEN(0, 0)）：点它只选行不折叠/加书签
        _scintilla.Margins[0].Sensitive = false;
        // 1: 折叠
        // Symbol 类型 = 图标边距（SC_MARGIN_SYMBOL），只画掩码允许的 marker
        _scintilla.Margins[1].Type = MarginType.Symbol;
        _scintilla.Margins[1].Mask = SC_MASK_FOLDERS;
        _scintilla.Margins[1].Width = 16;
        // 折叠边距要能点（OnMarginClick 里 e.Margin == 1 的分支就靠这个开关才会触发）
        _scintilla.Margins[1].Sensitive = true;
        // 2: 书签
        _scintilla.Margins[2].Type = MarginType.Symbol;
        // 掩码只放 2 号位：这样书签列只出现我们加的那个圆点，不会被差异标记之类串门
        _scintilla.Margins[2].Mask = BookmarkMarkerMask;
        _scintilla.Margins[2].Width = 14;
        _scintilla.Margins[2].Sensitive = true;
        // 3+: 关闭
        // 宽度 0 = SCI_SETMARGINWIDTHN(i, 0)，边距直接不占位；Scintilla 默认 5 个边距，把多余的收掉
        for (int i = 3; i < 5; i++)
        {
            _scintilla.Margins[i].Width = 0;
        }

        // ===== 折叠标记样式 =====
        // SCI_SETFOLDFLAGS：LineAfterContracted 让折叠后在折叠行下方画一条横线，和 ND2 一致
        _scintilla.SetFoldFlags(FoldFlags.LineAfterContracted);

        // 7 个折叠槽位逐个配图标与配色（SCI_SETMARKERSYMBOL / SCI_SETMARKERFORE / SCI_SETMARKERBACK），
        // 连线部分用 VLine/TCorner/LCorner 拼出树状竖线；父行开合用带连线的方框 +/-
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEROPEN], MarkerSymbol.BoxMinusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDER], MarkerSymbol.BoxPlusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERSUB], MarkerSymbol.VLine);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERTAIL], MarkerSymbol.LCorner);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEREND], MarkerSymbol.BoxPlusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEROPENMID], MarkerSymbol.BoxMinusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERMIDTAIL], MarkerSymbol.TCorner);

        // ===== 书签标记样式 =====
        // 2 号 marker 画成实心书签图标，前景/背景都涂琥珀色：边距里看到的就是它
        var bookmark = _scintilla.Markers[BookmarkMarkerIndex];
        bookmark.Symbol = MarkerSymbol.Bookmark;
        // SetForeColor/SetBackColor 对应 SCI_SETMARKERFORE / SCI_SETMARKERBACK
        bookmark.SetForeColor(DrawingColor.FromArgb(255, 220, 160, 60));
        bookmark.SetBackColor(DrawingColor.FromArgb(255, 220, 160, 60));

        // ===== 自动折叠 =====
        // SCI_SETAUTOMATICFOLD：Show=新文档自动展开到可见、Click=点边距即折叠、Change=输入时同步折叠层级
        _scintilla.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;

        // ===== 空白字符 / 行尾显示 =====
        // WhitespaceSize = SCI_SETWHITESPACESIZE，1 像素宽的点；具体显不显示由 BlankShow 位域控制
        _scintilla.WhitespaceSize = 1;
        // 出厂全关（ViewWhitespace=SCWS_INVISIBLE、ViewEol=false），对应 ND2 blank 键默认 0
        _scintilla.ViewWhitespace = WhitespaceMode.Invisible;
        _scintilla.ViewEol = false;

        // ===== 查找高亮 indicator =====
        // indicator 是「叠在文字上的装饰层」（SCI_SETINDICATORCURRENT + SCI_INDICATORFILLRANGE），
        // 不改样式、不影响撤销栈，所以清除高亮只要清区间，不用重排文本
        var indicator = _scintilla.Indicators[FindIndicatorIndex];
        indicator.Style = IndicatorStyle.StraightBox;
        // Alpha=80 的半透明蓝框：SCI_INDICSETFORE / SCI_INDICSETALPHA，叠在语法色上仍能看清字
        indicator.ForeColor = DrawingColor.FromArgb(0xFF, 0x2A, 0x7D, 0xD2);
        indicator.Alpha = 80;
        // Under=true 让这个 indicator 画在文字下方（而不是上方），否则会盖住字形
        indicator.Under = true;

        // ===== 标记色 indicator：槽位 n 用 indicator 20+n，颜色照 ND2 mark_style_1..5 的默认色 =====
        // 圆角框 + Alpha 130，比查找高亮更醒目；画在文字上方（Under=false）当「荧光笔」用
        for (int i = 0; i < MarkColorCount; i++)
        {
            var mark = _scintilla.Indicators[MarkIndicatorBase + i];
            mark.Style = IndicatorStyle.RoundBox;
            mark.ForeColor = DrawingColor.FromArgb(MarkColors[i]);
            mark.Alpha = 130;
            mark.Under = false;
        }

        // ===== 右键菜单 =====
        // ContextMenuStrip 是 WinForms 侧的菜单，Scintilla 自己处理 WM_CONTEXTMENU 后弹出它
        _scintilla.ContextMenuStrip = BuildContextMenu();

        // ===== Ctrl + 滚轮缩放 =====
        _scintilla.MouseWheel += OnMouseWheel;

        // 编辑区是 Win32 子 HWND，点击不会走 WPF 输入管线，主窗口只能靠这个通知补一次抬层
        _scintilla.MouseDown += (_, e) =>
        {
            EditorMouseDown?.Invoke();
            // ND2 在 mouseDoubleClickEvent 里先判 URL 热点（:2639），命中就直接开浏览器；
            // WinForms 的第二次按下走的是同一条消息，Clicks==2 就是它
            if (e.Button == MouseButtons.Left && e.Clicks == 2) OnEditorDoubleClick();
        };

        // ===== 事件 =====
        // 边距点击：折叠列/书签列都靠它（MarginClick 由 SCN_MARGINCLICK 转来，e.Margin 是边距序号）
        _scintilla.MarginClick += OnMarginClick;
        // 光标移动（SCN_CARET）：状态栏行列、位置历史都订阅它
        _scintilla.CursorChanged += OnCursorChanged;
        // UpdateUI（SCN_UPDATEUI）：改动/选区/可视样式变化都会来一次，是热点与括号高亮的挂载点
        _scintilla.UpdateUI += OnUpdateUI;
        // 文本增删（SCN_MODIFIED 的插入/删除分支）：用来把文档标成「脏」
        _scintilla.Insert += OnTextChanged;
        _scintilla.Delete += OnTextChanged;
        // 离开/回到保存点（SCI_SETSAVEPOINT 的两侧事件）：标题栏 ● 脏标记就靠这一对
        _scintilla.SavePointLeft += OnSavePointLeft;
        _scintilla.SavePointReached += OnSavePointReached;
        // 快捷键：焦点在编辑器里时 WinForms 先拿到按键，命中的命令转给主窗口执行
        _scintilla.KeyDown += OnScintillaKeyDown;
        // 字符输入回调（SCN_CHARADDED）：回车自动缩进在这里做
        _scintilla.CharAdded += OnCharAdded;
    }

    /// <summary>
    /// 把全局 Tab 设置写进本编辑器（ND2 scintillaeditview.cpp:1055-1058：
    /// SCI_SETTABWIDTH(s_tabLens) 与 setIndentationsUseTabs(!s_noUseTab)）
    /// </summary>
    public void ApplyTabSettings()
    {
        // 每次现取配置，不缓存：配置面板改完立刻对所有已打开的编辑器生效
        var s = SettingsService.Current;
        // TabWidth = SCI_SETTABWIDTH，一个 Tab 制表位占多少「列」（不是像素，列宽由字体推算）
        _scintilla.TabWidth = s.TabWidth;
        // UseTabs = SCI_SETUSETABS：true 时缩进插入真正的 \t，false 时按 TabWidth 展开成空格
        _scintilla.UseTabs = !s.ReplaceTabWithSpace;
    }

    /// <summary>编辑器内按下的快捷键（命令名），由主窗口处理</summary>
    /// <remarks>字符串是命令 id（与菜单/工具栏共用同一套，如 "saveas"、"dupline"），主窗口按 id 分发到具体动作。</remarks>
    public event EventHandler<string>? ShortcutPressed;

    // 外部文件拖放接收器：挂在 Scintilla 子窗口上，句柄重建（浮动/停靠）后要重挂
    /// <summary>当前挂着的拖放接收器（子类化 Scintilla 的 HWND 收 WM_DROPFILES）；null 表示还没挂或已摘除</summary>
    private FileDropSink? _dropSink;

    // 括号配对高亮当前填了的格子（指示器编号 + 位置），光标挪走时只清这几个位置
    /// <summary>本轮括号高亮实际填过的 indicator 区间（编号, 起始码元位）。只记自己填的两格，清除时定点擦，绝不整篇 IndicatorClearRange（那会把查找高亮一起抹掉）</summary>
    private readonly List<(int Index, int Pos)> _braceFill = new();

    /// <summary>外部文件被拖到本编辑器上并松手（主窗口据此打开文件）</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>
    /// 给 Scintilla 子窗口挂上外部文件拖放接收器（<see cref="FileDropSink"/> 走
    /// DragAcceptFiles + WM_DROPFILES，绕开 Scintilla 自己注册的 OLE IDropTarget）。
    /// </summary>
    private void AttachFileDropSink()
    {
        // 没有句柄就啥也做不了：等 HandleCreated 事件再来（构造函数里已经订阅）
        if (!_scintilla.IsHandleCreated) return;
        _dropSink?.Release();          // 句柄重建时会再次进来，先把旧接收器摘掉
        _dropSink = new FileDropSink(_scintilla);
        // 把接收器解析出的路径往上转一层，交给主窗口决定「新标签打开」还是「追加到当前文档」
        _dropSink.FilesDropped += paths => FilesDropped?.Invoke(paths);
    }

    /// <summary>
    /// 编辑器内按键 → 命令 id 的映射表。命中就发 <see cref="ShortcutPressed"/> 并吞掉按键，
    /// 没命中的（普通输入、方向键、原生 Ctrl+Z 之类）原样留给 Scintilla 处理。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">WinForms 按键事件参数，命中命令时置 Handled/SuppressKeyPress 吃掉它</param>
    private void OnScintillaKeyDown(object? sender, System.Windows.Forms.KeyEventArgs e)
    {
        // 一条 if-else 链按「修饰键组合从细到粗」排：先判 Ctrl+Shift+S 再判 Ctrl+S，
        // 否则 Ctrl+S 分支会把 Ctrl+Shift+S 抢先吃掉（else-if 顺序即优先级）
        string? cmd = null;

        if (e.Control && e.Shift && e.KeyCode == Keys.S) cmd = "saveas";
        else if (e.Control && e.KeyCode == Keys.S) cmd = "save";
        else if (e.Control && e.KeyCode == Keys.N) cmd = "new";
        else if (e.Control && e.KeyCode == Keys.O) cmd = "open";
        else if (e.Control && e.KeyCode == Keys.F) cmd = "find";
        else if (e.Control && e.KeyCode == Keys.G) cmd = "gotoline";
        else if (e.Control && e.KeyCode == Keys.W) cmd = "closefile";
        else if (e.Control && e.KeyCode == Keys.T) cmd = "theme";
        // ND2 的三条注释快捷键（shortcutkeymgr.cpp:112-114），焦点在编辑器时由这里转给主窗口
        else if (e.Control && e.Shift && e.KeyCode == Keys.K) cmd = "cbkcom";
        else if (e.Control && e.KeyCode == Keys.Q) cmd = "linecom";
        else if (e.Control && e.KeyCode == Keys.K) cmd = "bkcom";
        // 缩放三档：+/- 主键盘与小键盘两套键码都要认（Keys.Oemplus 与 Keys.Add 在不同布局下分别出现）
        else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) cmd = "zoomin";
        else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) cmd = "zoomout";
        else if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)) cmd = "zoomreset";
        // ND2「行编辑」在 .ui 里定义的四个快捷键，焦点在编辑器时也得照样走主窗口那套（只读判断在宿主侧）
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.D) cmd = "dupline";
        else if (e.Control && e.Shift && e.KeyCode == Keys.Up) cmd = "movelineup";
        else if (e.Control && e.Shift && e.KeyCode == Keys.Down) cmd = "movelinedown";
        else if (e.Control && e.Alt && e.Shift && e.KeyCode == Keys.Return) cmd = "blankbelow";
        else if (e.Control && e.Alt && e.KeyCode == Keys.Return) cmd = "blankabove";
        // 书签三条共用 F2，靠修饰键区分：注意 Ctrl+F2 要在裸 F2 之前判，否则会被后者抢先
        else if (e.KeyCode == Keys.F2 && e.Control) cmd = "bookmark";            // ND2：Ctrl+F2 设置/取消
        else if (e.KeyCode == Keys.F2 && e.Shift) cmd = "bookmarkprev";          // ND2：Shift+F2 上一书签
        else if (e.KeyCode == Keys.F2) cmd = "bookmarknext";                     // ND2：F2 下一书签
        else if (e.KeyCode == Keys.F3 && e.Shift) cmd = "findprev";
        else if (e.KeyCode == Keys.F3) cmd = "findnext";

        if (cmd != null)
        {
            // Handled 只挡 WinForms 层的后续按键逻辑，Scintilla 的原生字符处理还得靠
            // SuppressKeyPress 一起置 true 才不会把「\t」之类的键当字符吞进去
            ShortcutPressed?.Invoke(this, cmd);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // ==================== 回车自动缩进 ====================
    // ND2 无条件 setAutoIndent(true)（scintillaeditview.cpp:1149），实现在 QScintilla：
    // 字符已经插进缓冲区之后再处理，且只在「无选区、光标不在文档开头」时生效
    // （qsciscintilla.cpp:245-284 handleCharAdded）。本仓库里没有任何词法器覆写
    // blockStart/blockEnd/blockStartKeyword，autoIndentStyle() 恒为 AiMaintain，
    // 所以所有语言走的都是 maintainIndentation 这一条路。

    /// <summary>
    /// 字符输入回调（SCN_CHARADDED）：字符已经进缓冲区之后再判自动缩进，
    /// 顺序与 QScintilla 的 handleCharAdded 一致——先插后处理，缩进才能看到刚敲的回车。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">刚输入的字符（Unicode 码点，代理对可能被拆成两次回调）</param>
    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        int pos = _scintilla.SelectionStart;
        // QScintilla 的两个前置条件：有选区时不自动缩进（用户想覆盖选中内容），
        // 光标在文档开头（pos==0）时上面没有「参照行」可抄，也直接放过
        if (pos != _scintilla.SelectionEnd || pos == 0) return;
        MaintainIndentation(e.Char, pos);
    }

    /// <summary>照抄 QsciScintilla::maintainIndentation：沿用最近一条非空行的缩进</summary>
    /// <param name="ch">刚插入的字符；只有 \r 与 \n 会触发缩进延续，其余一律原样返回</param>
    /// <param name="pos">插入后光标所在的托管码元位（此时回车已在文档里，光标在新行行首）</param>
    private void MaintainIndentation(int ch, int pos)
    {
        // 只处理回车：CRLF 模式下会先来 \r 后来 \n，两次都跑这套逻辑，结果一样所以无害
        if (ch != '\r' && ch != '\n') return;

        // 新光标所在行就是「要抄缩进的那一行」，参照物在它上面
        int currLine = _scintilla.LineFromPosition(pos);

        // 向上找最近一条「有内容」的行（LineContentEndPos > 行首 = 这行不是空行）；
        // 空行不配当参照，否则连着敲几个回车会把缩进一层层吞掉/放大
        int ind = 0;
        for (int line = currLine - 1; line >= 0; line--)
        {
            if (LineContentEndPos(line) > _scintilla.Lines[line].Position)
            {
                // Line.Indentation = SCI_GETLINEINDENTATION，返回缩进的「列数」（Tab 已按 TabWidth 折算）
                ind = _scintilla.Lines[line].Indentation;
                break;
            }
        }

        // ind==0 就不用折腾了：整篇往上都是空行时保持默认（新行零缩进）
        if (ind > 0) AutoIndentLine(pos, currLine, ind);
    }

    /// <summary>照抄 QsciScintilla::autoIndentLine：改缩进后把光标按同样的位移带过去</summary>
    /// <param name="pos">当前光标位置（托管码元位）</param>
    /// <param name="line">要设置缩进的行号（一般是回车后的新行）</param>
    /// <param name="indent">目标缩进列数，来自上面找到的参照非空行</param>
    private void AutoIndentLine(int pos, int line, int indent)
    {
        var ln = _scintilla.Lines[line];
        // 先量一下「缩进区的末尾」在哪（IndentPosition = SCI_GETLINEINDENTPOSITION，
        // 即本行第一个非空白字符的位置），改完再量一次，两者之差就是文本被推走了多少
        int posBefore = ln.IndentPosition;
        // Line.Indentation setter = SCI_SETLINEINDENTATION，按列数写入，
        // 具体填 Tab 还是空格由当前 UseTabs/TabWidth 决定（所以这里绝不要自己拼空白）
        ln.Indentation = indent;
        int posAfter = ln.IndentPosition;

        // 下面三种情况把光标同步位移，让光标始终贴在它原来「相对于文本」的位置上；
        // 剩下一种（缩进变小且光标落在被删掉的空白里）交给 Scintilla 自己收敛，newPos 保持 -1 不动光标
        int newPos = -1;
        if (posAfter > posBefore)
        {
            // 缩进变长：整行文本被往右推，光标跟着右移同样的量
            newPos = pos + (posAfter - posBefore);
        }
        else if (posAfter < posBefore && pos >= posAfter)
        {
            // 缩进变短：光标若在旧缩进区之前，只能顶到新的缩进区末尾；在之后的就整体左移
            newPos = pos >= posBefore ? pos + (posAfter - posBefore) : posAfter;
        }

        // 托管 SetSelection(caret, anchor) 底层是 SCI_SETSEL(anchor, caret)，两个参数给同一个值
        // 即「清空选区并把光标放到 newPos」
        if (newPos >= 0) _scintilla.SetSelection(newPos, newPos);
    }

    /// <summary>
    /// 构造编辑区右键菜单（ND2 contextUserDefineMenuEvent 的等价物）。
    /// 菜单项全部用「文本 + 点击回调」的简写形式，回调直接打本编辑器的公有方法，
    /// 不走命令 id——右键菜单不需要与工具栏/快捷键共享分发。
    /// </summary>
    /// <returns>可直接赋给 <c>Scintilla.ContextMenuStrip</c> 的菜单</returns>
    private ContextMenuStrip BuildContextMenu()
    {
        // ShowImageMargin=false：ND2 的菜单是无图标的紧凑样式
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        // 撤销/重做走 Scintilla 自己的撤销栈（SCI_UNDO / SCI_REDO），不要自己记账
        menu.Items.Add("撤销", null, (_, _) => _scintilla.Undo());
        menu.Items.Add("重做", null, (_, _) => _scintilla.Redo());
        menu.Items.Add(new ToolStripSeparator());
        // 剪贴板三项直接调 Scintilla 内建实现（SCI_CUT/COPY/PASTE），中文与选区编码由它自己处理
        menu.Items.Add("剪切", null, (_, _) => _scintilla.Cut());
        menu.Items.Add("复制", null, (_, _) => _scintilla.Copy());
        menu.Items.Add("粘贴", null, (_, _) => _scintilla.Paste());
        menu.Items.Add("全选", null, (_, _) => _scintilla.SelectAll());
        menu.Items.Add(new ToolStripSeparator());
        // ND2 contextUserDefineMenuEvent（scintillaeditview.cpp:1964-1972）追加的三条注释项，文案照 realcompare_zh.ts:7478-7489
        // 三条都只传一个动作枚举，具体注释符号由 CommentSymbols 决定（见 EditorControl.CommentOps.cs）
        menu.Items.Add("添加/删除当行注释", null, (_, _) => Comment(CommentAction.AddDelLineComment));
        menu.Items.Add("区块注释", null, (_, _) => Comment(CommentAction.AddBlockComment));
        menu.Items.Add("清除区块注释", null, (_, _) => Comment(CommentAction.DelBlockComment));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("转成大写", null, (_, _) => ChangeCase(CaseMode.Upper));
        menu.Items.Add("转成小写", null, (_, _) => ChangeCase(CaseMode.Lower));
        menu.Items.Add(new ToolStripSeparator());
        // 书签与「展开/折叠」两项针对的是光标当前行，所以现场取 CurrentLine 而不是记住某一行
        menu.Items.Add("切换书签", null, (_, _) =>
        {
            int line = CurrentLine;
            ToggleBookmark(line);
            // 自己加的书签也要通知主窗口，书签列表/状态栏才同步（边距点击那条路径同理）
            BookmarkToggled?.Invoke(this, line);
        });
        // Line.ToggleFold = SCI_TOGGLEFOLD(行号)，等价于点折叠边距
        menu.Items.Add("展开/折叠", null, (_, _) => _scintilla.Lines[CurrentLine].ToggleFold());

        return menu;
    }

    /// <summary>
    /// Ctrl + 滚轮缩放（ND2 在 wheelEvent 里判 modifiers() &amp; Qt::ControlModifier）。
    /// 未按住 Ctrl 时不做任何事，滚轮继续交给 Scintilla 滚动文档。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">滚轮事件；只用 Delta（一个刻度 ±120），正数放大、负数缩小</param>
    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // ModifierKeys 是全局按键状态（不是事件里的字段），WinForms 滚轮事件的 Modifiers 在部分
        // 触控板驱动上不可靠，所以这里统一读一次进程级修饰键
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            // ZoomLevel 的 setter 自带 -10..30 夹取与 ZoomChanged 通知，这里只管加一减一
            ZoomLevel += e.Delta > 0 ? 1 : -1;
        }
    }

    /// <summary>
    /// 折叠标记的统一配色：图标本身浅灰白、底色中灰，这样树状竖线在明暗主题下都看得清
    /// （ND2 也是在 setFoldMarkerStyle 里一次性把七个槽位刷成同一套色）。
    /// </summary>
    /// <param name="marker">要配置的 marker 对象（<c>Scintilla.Markers[i]</c>）</param>
    /// <param name="symbol">该槽位要画的图标（SCI_SETMARKERSYMBOL）</param>
    private static void ConfigureFoldMarker(Marker marker, MarkerSymbol symbol)
    {
        marker.Symbol = symbol;
        // SetForeColor 定图标色，SetBackColor 定边距格底色，两个都对齐 ND2 的灰阶
        marker.SetForeColor(DrawingColor.FromArgb(255, 240, 240, 240));
        marker.SetBackColor(DrawingColor.FromArgb(255, 160, 160, 160));
    }

    /// <summary>暴露原生 Scintilla 控件：主窗口要发托管没封装的 SCI_ 消息、或拿子 HWND 时用（谨慎直接改文档）</summary>
    public Scintilla Scintilla => _scintilla;

    // ==================== 事件 ====================

    /// <summary>光标位置/选区变化（CursorChanged 与 UpdateUI 都会发），主窗口用它刷状态栏行列与选区字节数</summary>
    public event EventHandler? CursorPositionChanged;
    /// <summary>文档内容变化（插入或删除），主窗口用它把标签页标脏</summary>
    public event EventHandler? TextModified;
    /// <summary>保存点变化：true 表示回到已保存状态（应去掉 ●），false 表示离开保存点</summary>
    public event EventHandler<bool>? SavePointChanged;
    /// <summary>某行的书签被加上或去掉（参数是行号），主窗口用它同步书签列表与跳转记录</summary>
    public event EventHandler<int>? BookmarkToggled;

    /// <summary>
    /// 边距点击（SCN_MARGINCLICK）：按边距序号分流到折叠或书签。
    /// 0 号行号边距在 SetupEditor 里设成不敏感，根本不会走到这里。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">含 Margin（边距序号）与 Position（被点位置的托管码元位）</param>
    private void OnMarginClick(object? sender, MarginClickEventArgs e)
    {
        // Position → 行号（SCI_LINEFROMPOSITION）：点的是边距上的空白，但落点在文字行内，换算即得行
        int line = _scintilla.LineFromPosition(e.Position);

        if (e.Margin == 1)
        {
            // 折叠边距
            _scintilla.Lines[line].ToggleFold();
        }
        else if (e.Margin == 2)
        {
            // 书签边距
            ToggleBookmark(line);
            BookmarkToggled?.Invoke(this, line);
        }
    }

    /// <summary>光标变化转发（SCI_CARET 事件 → WPF 侧订阅者），本身不做计算</summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">空事件参数（未使用）</param>
    private void OnCursorChanged(object? sender, EventArgs e)
    {
        CursorPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// UpdateUI（SCN_UPDATEUI）总入口：文档/选区/可视样式任一变化都会进来，是「重算派生显示」的地方。
    /// 四件事的顺序不能随意换，见方法体注释。
    /// </summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">含变化类型位掩码；这里不区分，一律全量重算</param>
    private void OnUpdateUI(object? sender, UpdateUIEventArgs e)
    {
        // 先记账位置历史，再算别的：回退栈要抓到「这次移动之前/之后的位置」，晚做就漏
        TrackPosHistory();
        // ND2 的 handleUpdateUI 顺序是 cursorPosChange → addHotSpot → braceMatch（qsciscintilla.cpp:3745-3766）
        AddHotSpot();
        UpdateBraceMatch();
        // 最后通知外部刷状态栏：此时高亮已就绪，UI 拿到的是最终态
        CursorPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 位置历史入栈：ND2 的 cursorPosChange 只在「 caret 变了且行号也变了」时才 emit
    /// （qsciscintilla.cpp:3747-3760），所以同一行内横向移动不入栈；去重只看 m_curPos。
    /// </summary>
    private void TrackPosHistory()
    {
        // 第一道闸：行号没变就整段跳过（左右移动、Home/End 都不该污染回退栈）
        int line = _scintilla.CurrentLine;
        if (line == _lastTrackedLine) return;
        _lastTrackedLine = line;

        // 第二道闸：同一次跳转可能被 UpdateUI 回调多次，位置相同就只记一次
        int pos = _scintilla.CurrentPosition;
        if (pos == _curPos) return;

        // Add 在 List 尾部 = 栈顶（最新）；元素是托管码元位，和 CurrentPosition 同一口径
        _prePosRecord.Add(pos);
        // 超深度时 ND2 丢最旧的一条（RemoveAt(0)），保证栈顶永远是「最近经过的位置」
        if (_prePosRecord.Count >= MaxPreNextTimes) _prePosRecord.RemoveAt(0);
    }

    /// <summary>
    /// 回退一个位置（ND2 gotoPrePos）：栈里至少 2 条才动——最后一条就是「现在站的地方」；
    /// 弹顶塞进前进栈头部，再跳到新栈顶。返回 false 表示没有前一个位置了（调用方蜂鸣）。
    /// </summary>
    /// <returns>成功跳转返回 true；回退栈见底返回 false（主窗口据此蜂鸣提示）</returns>
    public bool GotoPrePos()
    {
        // 只有 1 条时那条就是当前位置，没有「更前面」可回
        if (_prePosRecord.Count <= 1) return false;

        // 弹栈顶（当前位置）→ 压进前进栈头部，保持前进栈也是「最近一次刚跳过的在前」
        int v = _prePosRecord[^1];
        _prePosRecord.RemoveAt(_prePosRecord.Count - 1);
        _nextPosRecord.Insert(0, v);
        // 前进栈同样限深，超了丢尾部（最旧的一条）
        if (_nextPosRecord.Count >= MaxPreNextTimes) _nextPosRecord.RemoveAt(_nextPosRecord.Count - 1);

        // 跳新栈顶；此时栈顶还没出栈，下一次回退会把它当「当前位置」再弹进前进栈
        JumpToPos(_prePosRecord[^1]);
        return true;
    }

    /// <summary>前进一个位置（ND2 gotoNextPos）：前进栈只逐条消耗，从不整体清空</summary>
    /// <returns>前进栈里有记录并成功跳转返回 true；没有可前进的位置返回 false</returns>
    public bool GotoNextPos()
    {
        if (_nextPosRecord.Count == 0) return false;

        // 前进栈是「头部即下一个」：取 [0] 并在这里不做限深（ND2 同款写法），
        // 回补进回退栈时也不判重复，靠 JumpToPos 设置 _curPos 挡住紧随的 UpdateUI
        int pos = _nextPosRecord[0];
        _nextPosRecord.RemoveAt(0);
        _prePosRecord.Add(pos);
        JumpToPos(pos);
        return true;
    }

    // C# 里 UpdateUI 是同步回调，必须在移动光标之前先记下 _curPos，否则跳过去会被自己再压一次栈
    /// <summary>
    /// 真正落地的位置跳转：先更新去重基准 <see cref="_curPos"/>，再动光标。
    /// 顺序颠倒的话，JumpToPos 触发的 UpdateUI 会把这个位置当成「新移动」重复入栈。
    /// </summary>
    /// <param name="pos">目标位置（托管 UTF-16 码元位，来自双栈记录）</param>
    private void JumpToPos(int pos)
    {
        _curPos = pos;
        _scintilla.CurrentPosition = pos;       // SCI_GOTOPOS 自带把光标滚进可视区
    }

    /// <summary>
    /// 光标贴到括号边上时点亮配对的另一半（配色与匹配模式对齐参考编辑器）：
    /// 宽松匹配——光标上、前 1、前 2 个字符都算贴着；包含 () [] {} &lt;&gt;（HTML 标签也配对）。
    /// 配上的另一半垫方框底色，配不上的那只把字符画成警示色。
    /// </summary>
    private void UpdateBraceMatch()
    {
        var sci = _scintilla;
        // 全部位置都是托管 UTF-16 码元位：GetCharAt 返回码点，下面的 +/-1 也按码元算，
        // 与原生 SCI_GETCHARAT 的字节口径差一个换算（封装层负责）
        var caret = sci.CurrentPosition;
        int bracePos = -1;

        // 三个探测点依次是「光标右侧第一个字符」「光标左侧」「左侧再左一个」：
        // 打字时光标通常在括号右边，选词/跳行时可能正好跨过去，ND2 也是这三档宽松判定
        if (caret < sci.TextLength && IsBrace((char)sci.GetCharAt(caret))) bracePos = caret;
        else if (caret > 0 && IsBrace((char)sci.GetCharAt(caret - 1))) bracePos = caret - 1;
        else if (caret > 1 && IsBrace((char)sci.GetCharAt(caret - 2))) bracePos = caret - 2;

        // 先把上一轮的高亮擦掉再决定新的：光标从括号上移开时必须恢复原样
        ClearBraceFill();

        if (bracePos < 0) return;

        // SCI_BRACEMATCH：找与 bracePos 处括号配对的另一个括号，找不到返回 -1（InvalidPosition）
        // 注意封装层的 brace 集合若不认 &lt;&gt;，尖括号会一律走「配不上」分支，表现为警示色
        var match = sci.BraceMatch(bracePos);
        var bad = match < 0;
        // 配不上时把「警示色」画在自己这一只上；配上时只画两端的括号本身
        var other = bad ? bracePos : match;

        // 指示器只画在已着色的区域上，先把两个括号附近的着色补齐，避免刚载入的大文件里框不出来
        // SCI_COLOURIZE(start, length)：立即让词法器对这段区间重新着色；
        // Math.Min/Abs 是为了兼容 match 在 bracePos 左边的情况（区间可能被反着给出）
        sci.Colorize(Math.Min(bracePos, other), Math.Abs(bracePos - other) + 1);

        // 配对成功用 9 号指示器（TextFore 改字色），配不上用 10 号，两者颜色都由主题决定
        var fillIndex = bad ? ThemeService.BraceBadForeIndicatorIndex : ThemeService.BraceIndicatorIndex;
        sci.Indicators[fillIndex].ForeColor =
            bad ? ThemeService.BraceBadFore : ThemeService.BraceMatchedFore;
        // IndicatorCurrent = SCI_SETINDICATORCURRENT，之后 FillRange 才知道画哪一层
        sci.IndicatorCurrent = fillIndex;
        // IndicatorFillRange(pos, 1) = SCI_INDICATORFILLRANGE：只盖住一个字符宽
        sci.IndicatorFillRange(bracePos, 1);
        _braceFill.Add((fillIndex, bracePos));
        if (!bad)
        {
            // 配对的另一半同样盖一格；配不上时不重复画自己
            sci.IndicatorFillRange(match, 1);
            _braceFill.Add((fillIndex, match));
        }
    }

    /// <summary>只清掉自己填的那两个格子，不动别的指示器</summary>
    private void ClearBraceFill()
    {
        // 逐个定点 SCI_INDICATORCLEARRANGE：直接整篇清会把查找高亮/标记色一起抹掉
        foreach (var (index, pos) in _braceFill)
        {
            _scintilla.IndicatorCurrent = index;
            _scintilla.IndicatorClearRange(pos, 1);
        }
        _braceFill.Clear();
    }

    /// <summary>是不是括号字符（含尖括号，跟 ND2 的宽松口径一致）；用于决定要不要尝试配对</summary>
    /// <param name="c">待判定的字符</param>
    /// <returns>是 () [] {} &lt;&gt; 之一返回 true</returns>
    private static bool IsBrace(char c) => c is '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>';

    /// <summary>文档内容变化转发（Insert/Delete 两个事件共用），主窗口据此打脏标记</summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">空事件参数（未使用）</param>
    private void OnTextChanged(object? sender, EventArgs e)
    {
        TextModified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>离开保存点（输入了新内容）：转发 false，标题与标签页显示 ●</summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">空事件参数（未使用）</param>
    private void OnSavePointLeft(object? sender, EventArgs e)
    {
        SavePointChanged?.Invoke(this, false);
    }

    /// <summary>回到保存点（撤销到上次保存的状态，或刚执行过 SetSavePoint）：转发 true</summary>
    /// <param name="sender">触发事件的 Scintilla 控件（未使用）</param>
    /// <param name="e">空事件参数（未使用）</param>
    private void OnSavePointReached(object? sender, EventArgs e)
    {
        SavePointChanged?.Invoke(this, true);
    }

    // ==================== 内容 ====================

    /// <summary>
    /// 整篇灌入内容（新建/打开/重载文档走这条）。注意这是「推倒重来」：
    /// 撤销缓冲被清空、保存点重设，调用方之后不该再期待历史可用。
    /// </summary>
    /// <param name="content">要显示的全文（.NET 字符串，由封装层编码成文档的代码页/UTF-8）</param>
    public void LoadContent(string content)
    {
        // SCI_CLEARALL：清空文档、撤销栈、选区与所有标记（书签/折叠状态一起没了，重载语义如此）
        _scintilla.ClearAll();
        // SCI_ADDTEXT：在光标处插入（刚 ClearAll 完，光标必在 0），比重新赋值 Text 少一次全量重建
        _scintilla.AddText(content);
        // SCI_EMPTYUNDOBUFFER：载入历史不该被撤销回来，否则 Ctrl+Z 能把文件倒回空文档
        _scintilla.EmptyUndoBuffer();
        // 载入即视为已保存状态，否则启动后每个标签都带 ● 脏标记
        _scintilla.SetSavePoint();
    }

    /// <summary>取全文（SCI_GETTEXTLEN + SCI_GETTEXT，UTF-8 字节会被解回 .NET 字符串）</summary>
    /// <returns>当前文档全文</returns>
    public string GetContent()
    {
        return _scintilla.Text;
    }

    // ==================== 列编辑 ====================

    /// <summary>选区覆盖的行范围与列位置；选区停在下一行行首时不算那行</summary>
    /// <returns>
    /// 四元组：起始行、结束行（均含）、选区起点的列、光标的列。
    /// 列由 GetColumn（SCI_GETCOLUMNOFPOSITION）按 Tab 展开后的「视觉列」给出，不是字符序号，
    /// 所以整行复制/列模式插入要用它而不是位置相减。
    /// </returns>
    public (int StartLine, int EndLine, int StartColumn, int CaretColumn) GetColumnEditContext()
    {
        // SelectionStart/End 是排序后的锚点与光标（min/max），不反映拖动方向，这里只关心覆盖范围
        int start = _scintilla.SelectionStart, end = _scintilla.SelectionEnd;
        int startLine = _scintilla.LineFromPosition(start);
        int endLine = _scintilla.LineFromPosition(end);
        // 拖选到下一行行首时，行首位置属于下一行，但用户没选中它的任何字符——ND2 也不算这行
        if (endLine > startLine && end == _scintilla.Lines[endLine].Position) endLine--;
        return (startLine, endLine, _scintilla.GetColumn(start), _scintilla.GetColumn(_scintilla.CurrentPosition));
    }

    /// <summary>在指定行、列插入文本（列超出该行长度时贴到行尾）</summary>
    /// <param name="line">目标行号（0 基）</param>
    /// <param name="column">视觉列号，来自 <see cref="GetColumnEditContext"/></param>
    /// <param name="text">要插入的文本</param>
    public void InsertColumnText(int line, int column, string text)
    {
        var ln = _scintilla.Lines[line];
        // ln.Position（SCI_POSITIONFROMLINE）+ 列偏移：Math.Min 夹住行长度，列比行还靠右时贴行尾插入；
        // 这里的「列」在本方法里按码元位偏移使用（Line.Length 含行尾符，越界由 Min 兜住）
        _scintilla.InsertText(ln.Position + Math.Min(column, ln.Length), text);
    }

    /// <summary>开始一组撤销（SCI_BEGINUNDOACTION）：到 EndEditGroup 之前的所有改动在 Ctrl+Z 里算一步</summary>
    public void BeginEditGroup() => _scintilla.BeginUndoAction();

    /// <summary>结束一组撤销（SCI_ENDUNDOACTION），与 <see cref="BeginEditGroup"/> 成对使用，异常路径记得在 finally 里调</summary>
    public void EndEditGroup() => _scintilla.EndUndoAction();

    // ==================== 语法高亮 ====================

    /// <summary>
    /// 根据文件路径设置 lexer（使用 Lexilla 名称，大小写不敏感匹配）
    /// </summary>
    /// <param name="filePath">文件路径，只按后缀判断语言；尚未建句柄时先记下，等 HandleCreated 再装</param>
    public void SetLexerForFile(string filePath)
    {
        // 记下路径而不是立刻应用：句柄可能还没创建，这时候装词法器会被 Scintilla 丢弃
        _pendingFilePath = filePath;
        ApplyLexer();
    }

    /// <summary><see cref="SetLexerForFile"/> 存下来的待应用路径；句柄就绪后由 <see cref="ApplyLexer"/> 消费</summary>
    private string? _pendingFilePath;

    /// <summary>当前词法器名称（自行记录，不依赖 Scintilla 的读回）</summary>
    /// <remarks>装词法器失败会退回 "null"，所以这里的值一定等于控件里真正生效的那个；主题与缩进线都按它分支。</remarks>
    public string CurrentLexerName { get; private set; } = "null";

    /// <summary>
    /// 状态栏「语法」栏要显示的语言名。ND2 显示的是当前词法器的 lexerTag（ccnotepad.cpp:7939
    /// syncCurDocLexerToMenu），手动切语法后不会被选区刷新改回去；我们这边多个语言共用一个
    /// Lexilla 词法器（cpp 同时服务 C#/Java/Go…），光看 CurrentLexerName 反推不出来，所以单独记。
    /// </summary>
    /// <remarks>可写：用户在「语言」菜单手动切语法时由主窗口直接赋值，此时词法器可能不变、显示名变。</remarks>
    public string LanguageDisplay { get; set; } = "Text";

    /// <summary>按当前主题给编辑器上色</summary>
    public void ApplyTheme()
    {
        // 主题服务会先 StyleResetDefault 再逐样式刷色，并按 CurrentLexerName 决定语言专用样式
        ThemeService.ApplyToScintilla(_scintilla, CurrentLexerName);
    }

    /// <summary>
    /// 把待应用的路径落成真正的词法器：只在句柄就绪后干活，否则等 HandleCreated 回调再来。
    /// 装完一定要重刷主题与着色，否则会出现「有词法器但整篇一个色」。
    /// </summary>
    private void ApplyLexer()
    {
        // 两个前提缺一不可：没有句柄设了也白设，没有路径就不知道装哪种语言
        if (!_scintilla.IsHandleCreated || _pendingFilePath == null) return;

        SetLexerByNameInternal(LexerService.GetLexerName(_pendingFilePath), _pendingFilePath);
        // 显示名走另一套映射（后缀 → 用户看得懂的语言名），与 Lexilla 名不是一回事
        LanguageDisplay = LexerService.GetLanguageName(_pendingFilePath);
        ApplyTheme();
        RestyleNow();
    }

    /// <summary>
    /// 手动切语法（语言菜单/查找结果跳转/另存为改后缀都走这里）：装词法器 + 刷主题 + 整篇重着色。
    /// </summary>
    /// <param name="lexerName">Lexilla 词法器名（如 "cpp"、"html"），空串按纯文本处理</param>
    /// <param name="keywordPath">用户自定义关键字文件路径（ND2 的 keywords 配置），null 表示用内置关键字</param>
    public void SetLexerByName(string lexerName, string? keywordPath = null)
    {
        SetLexerByNameInternal(lexerName, keywordPath);
        // 顺序不能反：主题按「已生效的词法器」刷样式，先装词法器再刷色才不会用错映射表
        ApplyTheme();
        RestyleNow();
    }

    /// <summary>词法器换好后强制整篇重新着色，避免「先填文本后设词法器」时留着旧样式</summary>
    private void RestyleNow()
    {
        // SCI_COLOURIZE(0, 全文长度)：同步跑词法分析；空文档不用折腾
        if (_scintilla.TextLength > 0) _scintilla.Colorize(0, _scintilla.TextLength);
    }

    /// <summary>
    /// 只负责「装词法器 + 配属性」，不刷主题也不重着色——这两步留给调用方按顺序做，
    /// 免得 ApplyLexer 与 SetLexerByName 出现两套时序。
    /// </summary>
    /// <param name="lexerName">想要的 Lexilla 名；不在目录里会自动退回 "null"</param>
    /// <param name="keywordPath">自定义关键字文件路径，null 表示不注入</param>
    private void SetLexerByNameInternal(string lexerName, string? keywordPath)
    {
        // 空名在 Scintilla 里是非法值，统一按纯文本（"null" 词法器）处理
        if (string.IsNullOrEmpty(lexerName)) lexerName = "null";

        // 下发的名字若不在 Lexilla 目录里，Scintilla 只会「静默不装词法器」——
        // 文本照常显示但整篇没有颜色。所以装完必须读回校验，失败就老实退回纯文本。
        bool installed = TryInstallLexer(lexerName);
        if (!installed && !string.Equals(lexerName, "null", StringComparison.OrdinalIgnoreCase))
        {
            // 退回时再装一次，并允许失败：连 "null" 都装不上说明底层控件状态不对，
            // 此时只把 CurrentLexerName 记成 "null"，后面的颜色映射按纯文本走，不至于崩
            lexerName = "null";
            installed = TryInstallLexer(lexerName);
        }
        CurrentLexerName = installed ? lexerName : "null";

        // 注释符号跟着「真正生效的那个词法器」走，和 ND2 把符号挂在词法器实例上同一个口径
        CommentSymbols = LexerService.GetCommentSymbols(CurrentLexerName, keywordPath);

        // 注入关键字表（cpp 这类词法器不带 C#/JS/Go 等语言的关键字，不注入则关键字不上色）
        if (keywordPath != null)
        {
            var kw = LexerService.GetKeywords(keywordPath);
            if (kw != null)
            {
                // SetKeywords(0/1) = SCI_SETKEYWORDS：0 号表是主关键字（着色为 keyword），
                // 1 号表是次关键字（如类型名），两套关键字对应两套样式
                _scintilla.SetKeywords(0, kw.Primary);
                _scintilla.SetKeywords(1, kw.Secondary);
            }
        }

        // 启用折叠（大部分 Lexilla 词法器靠 fold 属性生成折叠层级）
        // SetProperty = SCI_SETPROPERTY，词法器在 lex* 里读这些名字；不设 fold=1 时边距里一根线都没有
        _scintilla.SetProperty("fold", "1");
        _scintilla.SetProperty("fold.comment", "1");
        _scintilla.SetProperty("fold.compact", "1");
        _scintilla.SetProperty("fold.html", "1");
        _scintilla.SetProperty("fold.hypertext.comment", "1");

        // 词法器换了，缩进参考线的模式可能跟着换（Python 那一档不一样），补一次
        ApplyIndentGuide();
    }

    /// <summary>下发词法器并读回校验：以控件里真正生效的那个为准</summary>
    /// <param name="lexerName">要装的 Lexilla 名</param>
    /// <returns>读回的名字与请求一致返回 true；被静默忽略或抛异常返回 false</returns>
    private bool TryInstallLexer(string lexerName)
    {
        // 首选 LexerName 属性（SCI_SETILEXER / Lexilla 名字表），现代封装都走这条
        try { _scintilla.LexerName = lexerName; }
        catch
        {
            // 部分版本/名字大小写差异下换用旧字符串属性
            try { _scintilla.LexerLanguage = lexerName; } catch { return false; }
        }

        // 装失败时 Scintilla 不报错，只是读回 "null"/旧值，所以必须比对；比大小写不敏感（名字表大小写混用）
        try
        {
            string readback = _scintilla.LexerLanguage ?? string.Empty;
            return string.Equals(readback, lexerName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ==================== 光标位置 ====================

    /// <summary>光标所在行号（SCI_GETCURRENTLINE，0 基），状态栏「行」与行级操作都用它</summary>
    public int CurrentLine => _scintilla.CurrentLine;

    /// <summary>
    /// 光标所在列（0 基字符偏移，不是视觉列）：位置减行首位置。
    /// 含 Tab 的要用 <see cref="GetColumnEditContext"/> 里的 GetColumn，两者口径不同。
    /// </summary>
    public int CurrentColumn => _scintilla.CurrentPosition - _scintilla.Lines[_scintilla.CurrentLine].Position;

    /// <summary>总行数（SCI_GETLINECOUNT）；纯文本恒有 1 行，空文档也是 1 而不是 0</summary>
    public int LineCount => _scintilla.Lines.Count;

    /// <summary>文档长度，托管口径 = UTF-16 码元个数（原生 TextLength 是 UTF-8 字节数，封装层已换算）</summary>
    public int TextLength => _scintilla.TextLength;

    /// <summary>选中文本（SCI_GETSELECTIONTEXT 一类）；无选区时为空串</summary>
    public string SelectedText => _scintilla.SelectedText;

    /// <summary>选中长度（码元数）。取绝对值是因为反向拖选时 End &lt; Start 也会出现</summary>
    public int SelectionLength => Math.Abs(_scintilla.SelectionEnd - _scintilla.SelectionStart);

    /// <summary>把焦点交回编辑区（浮动/停靠/切标签之后调用，防止按键落到 WPF 宿主上）</summary>
    public void FocusEditor()
    {
        _scintilla.Focus();
    }

    /// <summary>
    /// 跳转到指定行（ND2 的 gotoLine + 保证可见）：行号夹到 [0, 行数-1]，越界不抛异常。
    /// </summary>
    /// <param name="line">目标行号（0 基；「转到第 N 行」对话框传的是 N-1）</param>
    public void GoToLine(int line)
    {
        // 两端夹取而不是判异常：状态栏/搜索结果给的行号可能因文档刚被改动而失效
        if (line < 0) line = 0;
        if (line >= _scintilla.Lines.Count) line = _scintilla.Lines.Count - 1;
        _scintilla.Lines[line].Goto();
        // EnsureVisible（SCI_ENSUREVISIBLE）先把中间的折叠层展开，否则跳进被折叠的行会看不到光标
        _scintilla.Lines[line].EnsureVisible();
        // SCI_SCROLLCARET：确保光标所在行滚进可视区（Goto 只动光标不动视图）
        _scintilla.ScrollCaret();
    }

    /// <summary>选中某行内以「字符」为单位的区间；托管位置本来就是 UTF-16 码元，直接相加即可</summary>
    /// <param name="lineIndex">行号（0 基），越界直接忽略</param>
    /// <param name="charColumn">行内起始列（码元数，从 0 起）</param>
    /// <param name="charLength">要选中的码元个数</param>
    public void SelectInLine(int lineIndex, int charColumn, int charLength)
    {
        // 结果面板双击定位走的这条路：文档可能在这之间被改过，行号失效就放弃这次定位
        if (lineIndex < 0 || lineIndex >= _scintilla.Lines.Count) return;
        string body = LineBody(lineIndex);

        // 两个 Math.Clamp 把「列 + 长度」折进行内容长度：
        // 第二个按 body.Length - column 夹，保证不会出现 end 越过行尾（含 \r\n 就不该选）
        int column = Math.Clamp(charColumn, 0, body.Length);
        int length = Math.Clamp(charLength, 0, body.Length - column);
        int start = LineStartPos(lineIndex) + column;
        int end = start + length;

        // SetSelection 的 caret 参数已经是 end，再调 GotoPosition 会把选区压扁
        _scintilla.SetSelection(start, end);
        _scintilla.ScrollCaret();
    }

    // ==================== 书签 ====================

    /// <summary>给某行加书签（SCI_MARKERADD 2 号槽位），行号越界静默忽略</summary>
    /// <param name="line">目标行号（0 基）</param>
    public void AddBookmark(int line)
    {
        if (line >= 0 && line < _scintilla.Lines.Count)
            _scintilla.Lines[line].MarkerAdd(BookmarkMarkerIndex);
    }

    /// <summary>去掉某行的书签（SCI_MARKERDELETE）；本来没有书签也是这个调用，幂等</summary>
    /// <param name="line">目标行号（0 基）</param>
    public void RemoveBookmark(int line)
    {
        if (line >= 0 && line < _scintilla.Lines.Count)
            _scintilla.Lines[line].MarkerDelete(BookmarkMarkerIndex);
    }

    /// <summary>切换某行书签（Ctrl+F2 用）：有则删、无则加</summary>
    /// <param name="line">目标行号（0 基）</param>
    public void ToggleBookmark(int line)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return;
        var l = _scintilla.Lines[line];
        // MarkerGet = SCI_MARKERGET，返回该行所有 marker 的位掩码，所以必须与掩码取与而不是 ==
        if ((l.MarkerGet() & BookmarkMarkerMask) != 0)
            l.MarkerDelete(BookmarkMarkerIndex);
        else
            l.MarkerAdd(BookmarkMarkerIndex);
    }

    /// <summary>某行是否有书签</summary>
    /// <param name="line">待检查的行号（0 基）</param>
    /// <returns>该行带 2 号 marker 返回 true；行号越界返回 false</returns>
    public bool HasBookmark(int line)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return false;
        return (_scintilla.Lines[line].MarkerGet() & BookmarkMarkerMask) != 0;
    }

    /// <summary>
    /// 列出全部书签行（升序）。逐行问 SCI_MARKERGET，文档很大时别在 UpdateUI 里调它。
    /// </summary>
    /// <returns>有书签的行号列表，按行号升序</returns>
    public List<int> GetBookmarks()
    {
        var result = new List<int>();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if ((_scintilla.Lines[i].MarkerGet() & BookmarkMarkerMask) != 0)
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// 查找下一个书签行（从 fromLine+1 开始，含环绕）
    /// </summary>
    /// <param name="fromLine">出发行号（不含），一般是当前行</param>
    /// <returns>下一个带书签的行号；全文没有书签返回 null（调用方蜂鸣）</returns>
    public int? NextBookmark(int fromLine)
    {
        int count = _scintilla.Lines.Count;
        // 第一段：先往下找
        for (int i = fromLine + 1; i < count; i++)
        {
            if (HasBookmark(i)) return i;
        }
        // 第二段：到文档尾了，绕回开头再找到出发行为止（ND2 的书签跳转总是环绕）
        for (int i = 0; i <= fromLine && i < count; i++)
        {
            if (HasBookmark(i)) return i;
        }
        return null;
    }

    /// <summary>
    /// 查找上一个书签行（从 fromLine-1 开始，含环绕）
    /// </summary>
    /// <param name="fromLine">出发行号（不含）</param>
    /// <returns>上一个带书签的行号；没有书签返回 null</returns>
    public int? PreviousBookmark(int fromLine)
    {
        int count = _scintilla.Lines.Count;
        // 第一段：先往上找
        for (int i = fromLine - 1; i >= 0; i--)
        {
            if (HasBookmark(i)) return i;
        }
        // 第二段：到文档头了，从最后一行绕回来
        for (int i = count - 1; i >= fromLine && i >= 0; i--)
        {
            if (HasBookmark(i)) return i;
        }
        return null;
    }

    /// <summary>清除全部书签（SCI_MARKERDELETEALL 2 号槽位）；不动查找高亮与标记色</summary>
    public void ClearBookmarks()
    {
        _scintilla.Markers[BookmarkMarkerIndex].DeleteAll();
    }

    // ==================== 折叠 ====================

    /// <summary>折叠全部层级（SCI_FOLDALL 的收起动作，一次刷完整棵折叠树）</summary>
    public void CollapseAll()
    {
        _scintilla.FoldAll(FoldAction.Contract);
    }

    /// <summary>展开全部层级（SCI_FOLDALL 的展开动作）</summary>
    public void ExpandAll()
    {
        _scintilla.FoldAll(FoldAction.Expand);
    }

    /// <summary>折叠/展开光标所在行（SCI_FOLDTOGGLE），与点折叠边距等价</summary>
    public void FoldCurrentLine()
    {
        _scintilla.Lines[_scintilla.CurrentLine].ToggleFold();
    }

    // ==================== 查找替换 ====================

    /// <summary>上一次命中的零长匹配位置，用于下一次查找时强行前进一格（-1 表示没有）</summary>
    private int _zeroMatchPos = -1;

    /// <summary>
    /// 在 [startPos, endPos) 内查找，返回匹配区间的字节偏移。
    /// Scintilla 文档内部恒为 UTF-8，位置是字节偏移，所以必须用 TargetEnd 而不是
    /// 「起点 + 字符串长度」，否则含中文的匹配会选错范围。
    /// </summary>
    /// <remarks>
    /// 补一句口径说明：托管层负责在原生 UTF-8 字节位与 .NET UTF-16 码元位之间换算
    /// （同 <c>EditorControl.LineOps.cs</c> 里 LineContentEndPos 的说明），
    /// 所以本文件里拿到的 Start/End 一律按码元位使用，不要再自己去乘字节数。
    /// 「必须用 TargetEnd」这条约束与口径无关——needle 的字符数永远不等于它在文档里占的字节数。
    /// </remarks>
    /// <param name="condition">查找条件（文本、模式、大小写、全词）</param>
    /// <param name="startPos">搜索区间起点（含）</param>
    /// <param name="endPos">搜索区间终点（不含）</param>
    /// <param name="verb">同一条件用于哪个动作，决定正则零长匹配策略，见 <c>SearchCondition.ToScintillaFlags</c></param>
    /// <returns>命中区间 (Start, End)；没命中返回 null</returns>
    private (int Start, int End)? FindRange(SearchCondition condition, int startPos, int endPos,
                                            FindVerb verb = FindVerb.FindNext)
    {
        // EffectiveText：扩展模式（\n \t \xNN）先还原成真实字符，正则/普通模式原样
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return null;

        // SearchFlags = SCI_SETSEARCHFLAGS：包含 MatchCase/WholeWord/Regex 与正则空匹配三态位
        _scintilla.SearchFlags = condition.ToScintillaFlags(verb);
        // SCI_SETTARGETSTART / SCI_SETTARGETEND 圈定本次搜索范围（Target 是全局状态，调用方注意别嵌套用）
        _scintilla.TargetStart = startPos;
        _scintilla.TargetEnd = endPos;
        // SCI_SEARCHINTARGET：在 Target 区间找第一处，命中后 Target 会被改写成匹配区间
        int pos = _scintilla.SearchInTarget(needle);
        return pos >= 0 ? (pos, _scintilla.TargetEnd) : null;
    }

    /// <summary>
    /// 查找下一处/上一处。backward 对应 ND2 的 Backward 复选框（同一个「查找下一个」按钮换方向），
    /// wrap 对应 Wrap around：关掉后走到文档尽头就不再绕回另一端。
    /// </summary>
    /// <param name="condition">查找条件</param>
    /// <param name="wrap">找不到时是否绕回文档另一端继续</param>
    /// <param name="backward">true 表示向上找（ND2 的 Backward 勾选态）</param>
    /// <returns>命中并把选区落上返回 true；没命中返回 false（选区保持原样）</returns>
    public bool FindNext(SearchCondition condition, bool wrap, bool backward)
    {
        if (condition.IsEmpty) return false;

        // 注意 SelectionStart/End 是「区间两端（小,大）」而不是「锚点/光标」，
        // 所以 to（回找的上界）取的是区间头，from（往下找的下界）取区间尾
        int length = _scintilla.TextLength;
        int caret = _scintilla.SelectionStart;
        int tail = _scintilla.SelectionEnd;

        // 上一次命中的是零长匹配：ND2 dealWithZeroFound 把 startpos 强行加 1，否则同一个位置会被反复找到
        bool stepOverZero = _zeroMatchPos >= 0 && tail == _zeroMatchPos;
        _zeroMatchPos = -1;
        int from = stepOverZero ? tail + 1 : tail;
        int to = Math.Max(0, stepOverZero ? caret - 1 : caret);

        // 正向：先找 [from, 文档尾)，没有再按 wrap 决定要不要绕到 [0, to)；
        // 反向：对称地先找 [0, to) 里最后一处，再绕到整篇里最后一处。
        // LastInRange 而不是「反向 Target」，是因为正则空匹配与全词边界在反向扫描下行为不一致，
        // ND2 也是用正向扫描取最靠后的那一处来模拟「上一处」
        (int Start, int End)? hit = backward
            ? LastInRange(condition, 0, to) ?? (wrap ? LastInRange(condition, 0, length) : null)
            : FindRange(condition, from, length) ?? (wrap ? FindRange(condition, 0, to) : null);

        if (hit == null) return false;

        // 零宽匹配（如正则 ^）会把同一处反复找到，光标前移一格保证每次按键都有进展
        if (hit.Value.Start == hit.Value.End)
        {
            // 记下这个位置，下一次进来就走上面的 stepOverZero 分支
            _zeroMatchPos = hit.Value.Start;
            // SetSelection(同一位置, 同一位置) = SCI_SETSEL 空选区，等价于把光标放到匹配点
            _scintilla.SetSelection(hit.Value.Start, hit.Value.Start);
            _scintilla.ScrollCaret();
            // ND2 dealWithZeroFoundShowTip：零长匹配看不见，用 calltip 在原地说明一下
            // CallTipShow(pos, text) = SCI_CALLTIPSHOW，气泡钉在该位置的正下方
            _scintilla.CallTipShow(hit.Value.Start, ZeroLengthMatchTip);
            return true;
        }

        SelectRange(hit.Value.Start, hit.Value.End);
        return true;
    }

    /// <summary>
    /// 在 [startPos, endPos) 里找「最后一处」匹配，用来模拟反向查找。
    /// </summary>
    /// <param name="condition">查找条件</param>
    /// <param name="startPos">区间起点（含）</param>
    /// <param name="endPos">区间终点（不含）</param>
    /// <returns>区间里最靠后的那一处；没有则 null</returns>
    private (int Start, int End)? LastInRange(SearchCondition condition, int startPos, int endPos)
    {
        (int Start, int End)? best = null;
        int searchPos = startPos;

        // 每轮从上一处匹配的下一个字节继续，总扫描量仍是线性
        while (searchPos < endPos)
        {
            var found = FindRange(condition, searchPos, endPos);
            if (found == null) break;

            best = found;
            // 零长匹配时 End==Start，不 +1 就会在同一处转圈；非零长时直接跳到匹配之后
            searchPos = found.Value.End > found.Value.Start ? found.Value.End : found.Value.Start + 1;
        }
        return best;
    }

    /// <summary>把选区落到 [start, end) 并滚进可视区（SCI_SETSEL + SCI_SCROLLCARET）</summary>
    /// <remarks>
    /// 口径提醒：托管 <c>SetSelection(caret, anchor)</c> 的第一个形参是 **caret**（对应原生
    /// SCI_SETSEL 的 lParam），所以这里传 (start, end) 得到的是「锚点在 end、光标停在 start」。
    /// 选区间与高亮范围不受影响（SelectionStart/End 取的是两端排序后的 min/max），
    /// 只是紧接着的输入/方向键会朝右侧展开。
    /// </remarks>
    /// <param name="start">选区起点（托管码元位）</param>
    /// <param name="end">选区终点（托管码元位）</param>
    private void SelectRange(int start, int end)
    {
        _scintilla.SetSelection(start, end);
        _scintilla.ScrollCaret();
    }

    /// <summary>
    /// 统计全文匹配数（ND2 查找窗状态栏的「N 处匹配」）。不改动选区与撤销栈。
    /// </summary>
    /// <param name="condition">查找条件</param>
    /// <returns>匹配处数；条件为空返回 0</returns>
    public int CountMatches(SearchCondition condition)
    {
        if (condition.IsEmpty) return 0;

        int count = 0;
        string needle = condition.EffectiveText;
        // 计数用 FindNext 那套零长匹配策略（EMPTYMATCH_ALL），与 ND2 统计口径一致：
        // 一个位置能匹配多次的空正则按多处计
        _scintilla.SearchFlags = condition.ToScintillaFlags(FindVerb.FindNext);
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(needle) >= 0)
        {
            count++;
            // 零长匹配（正则 EMPTYMATCH_ALL 会给出）不前进就会原地打转，与 ND2 dealWithZeroFound 一样加 1
            int newStart = _scintilla.TargetEnd > _scintilla.TargetStart
                ? _scintilla.TargetEnd
                : _scintilla.TargetStart + 1;
            // 走到文档末尾就收工：TargetStart == TextLength 时再搜也搜不出东西，白跑一轮
            if (newStart >= _scintilla.TextLength) break;
            _scintilla.TargetStart = newStart;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return count;
    }

    /// <summary>
    /// ND2「提取正则结果到剪切板」取的就是每处匹配的文本本身（targstart..targend），
    /// 零长匹配不算结果，与 addCurFindRecord 里 getResult 分支的判断一致。
    /// </summary>
    /// <param name="condition">查找条件（一般是正则模式）</param>
    /// <returns>按出现顺序排列的匹配文本；没有匹配时为空列表</returns>
    public IReadOnlyList<string> CollectMatchTexts(SearchCondition condition)
    {
        var texts = new List<string>();
        foreach (var (start, end) in FindRanges(condition))
        {
            if (end <= start) continue;
            // GetTextRange(pos, len) = SCI_GETTEXT（截断到 len）：这里 len 也按码元数给，与 start/end 同口径
            texts.Add(_scintilla.GetTextRange(start, end - start));
        }
        return texts;
    }

    /// <summary>
    /// 替换当前选区内的第一处匹配（ND2「替换」按钮：只在选区内找，命中即换，然后由调用方再按一次查找下一个）。
    /// </summary>
    /// <param name="find">查找条件</param>
    /// <param name="replaceText">替换文本原始串（扩展/正则模式下会再处理一次）</param>
    /// <returns>替换成功返回 true；选区内没有匹配返回 false</returns>
    public bool ReplaceCurrent(SearchCondition find, string replaceText)
    {
        if (find.IsEmpty) return false;

        // 用 ReplaceNext 的零长匹配策略（NOTAFTERMATCH），避免正则替换在原地反复替换
        _scintilla.SearchFlags = find.ToScintillaFlags(FindVerb.ReplaceNext);
        // Target 直接取当前选区两端：SCI_SEARCHINTARGET 只在这一段里找，越界不会命中
        _scintilla.TargetStart = _scintilla.SelectionStart;
        _scintilla.TargetEnd = _scintilla.SelectionEnd;

        int pos = _scintilla.SearchInTarget(find.EffectiveText);
        if (pos < 0) return false;

        // PrepareReplacement：扩展模式还原 \n \t 等；正则模式还要把 $1 \1 之类的分组引用交给 Scintilla 解释
        string replacement = find.PrepareReplacement(replaceText);
        if (find.Mode == SearchMode.Regex)
            // SCI_REPLACETARGETRE：替换文本按正则语法展开分组引用
            _scintilla.ReplaceTargetRe(replacement);
        else
            // SCI_REPLACETARGET：字面替换整个 Target 区间
            _scintilla.ReplaceTarget(replacement);
        return true;
    }

    /// <summary>
    /// 全文替换（ND2「全部替换」）：整批动作包进一个撤销组，一次 Ctrl+Z 全部退回。
    /// </summary>
    /// <param name="find">查找条件</param>
    /// <param name="replaceText">替换文本原始串</param>
    /// <returns>实际替换的处数；条件为空返回 0</returns>
    public int ReplaceAll(SearchCondition find, string replaceText)
    {
        if (find.IsEmpty) return 0;

        int count = 0;
        string needle = find.EffectiveText;
        // 替换文本只预处理一次：每处都用同一个串
        string replacement = find.PrepareReplacement(replaceText);
        // 整批包成一个撤销单元（SCI_BEGINUNDOACTION/ENDUNDOACTION），finally 保证异常也配对
        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.SearchFlags = find.ToScintillaFlags(FindVerb.ReplaceNext);
            _scintilla.TargetStart = 0;
            _scintilla.TargetEnd = _scintilla.TextLength;

            while (_scintilla.SearchInTarget(needle) >= 0)
            {
                if (find.Mode == SearchMode.Regex)
                    _scintilla.ReplaceTargetRe(replacement);
                else
                    _scintilla.ReplaceTarget(replacement);
                count++;

                // ReplaceTarget 会把 target 挪到替换结果末尾，零长匹配时再往前让一格
                int newStart = _scintilla.TargetEnd > _scintilla.TargetStart
                    ? _scintilla.TargetEnd
                    : _scintilla.TargetStart + 1;
                // 剩余长度不足一处时提前退出；注意替换会让文档变长/变短，所以 TextLength 每轮重取
                if (newStart >= _scintilla.TextLength) break;
                _scintilla.TargetStart = newStart;
                _scintilla.TargetEnd = _scintilla.TextLength;
            }
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return count;
    }

    /// <summary>
    /// 一次性换掉整篇文本（对应 ND2 replaceAtBack 的 REPLACETARGET 写法）：
    /// 撤销栈里只留一个动作，且保留历史，不像 LoadContent 那样清空缓冲。
    /// </summary>
    /// <param name="newText">替换后的全文（例如外部修改重载、格式化结果）</param>
    public void ReplaceWholeText(string newText)
    {
        _scintilla.BeginUndoAction();
        try
        {
            // Target 覆盖 [0, 全文]，SCI_REPLACETARGET 就是把这一整段换成 newText
            _scintilla.TargetStart = 0;
            _scintilla.TargetEnd = _scintilla.TextLength;
            _scintilla.ReplaceTarget(newText);
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
    }

    // ==================== 剪贴板安全包装 ====================

    /// <summary>复制（剪贴板异常时静默失败，避免崩溃弹窗）</summary>
    public void SafeCopy()
    {
        // 剪贴板被别的进程锁住时 OLE 会抛 ExternalException，编辑器不能因此挂掉
        try { _scintilla.Copy(); } catch { }
    }

    /// <summary>剪切</summary>
    public void SafeCut()
    {
        try { _scintilla.Cut(); } catch { }
    }

    /// <summary>粘贴</summary>
    public void SafePaste()
    {
        // 粘贴走 Scintilla 内建实现（SCI_PASTE），编码转换与换行符归一化由它自己按文档设置处理
        try { _scintilla.Paste(); } catch { }
    }

    // ==================== 缩放 ====================

    /// <summary>字号变化通知（参数是新的 Scintilla zoom 值），主窗口用它回写设置与状态栏百分比</summary>
    public event EventHandler<int>? ZoomChanged;

    /// <summary>
    /// 缩放级别（SCI_SETZOOM / SCI_GETZOOM）：单位是「磅值增量」而不是百分比，0 为原始大小。
    /// ND2 的范围是 -9..18 一档档步进，这里放宽到 -10..30 并夹取，滚轮连拉也不会无限放大。
    /// </summary>
    public int ZoomLevel
    {
        // 读回的是控件当前真实值（主题重刷样式也可能改动它），不做本地缓存
        get => _scintilla.Zoom;
        set
        {
            int v = Math.Clamp(value, -10, 30);
            _scintilla.Zoom = v;
            // 即使值没变也发事件：主窗口靠它统一刷新显示，省得在这里判断差异
            ZoomChanged?.Invoke(this, v);
        }
    }

    /// <summary>放大一级（Ctrl+滚轮上 / Ctrl++ / 菜单「放大」）</summary>
    public void ZoomIn() => ZoomLevel += 1;

    /// <summary>缩小一级（Ctrl+滚轮下 / Ctrl+- / 菜单「缩小」）</summary>
    public void ZoomOut() => ZoomLevel -= 1;

    /// <summary>恢复原始字号（Ctrl+0）</summary>
    public void ResetZoom() => ZoomLevel = 0;

    // ==================== 显示选项 ====================

    /// <summary>
    /// ND2 的 blank 位域（ccnotepad.cpp:1059-1081 setEditShowBlankStatus）：
    /// 0 全关 / 1 只空格 / 2 只行尾 / 3 都开。空格走的是 WsVisible（一直可见），不是「缩进之后才显示」。
    /// </summary>
    public int BlankShow
    {
        // 读回时把两个独立开关重新拼成位域：只要空白「以任何形式可见」就算 bit0=1，
        // 因此 VisibleAfterIndent 一类的中间态读回来会当成 VisibleAlways（与 ND2 只有两态的存储格式对齐）
        get => (_scintilla.ViewWhitespace != WhitespaceMode.Invisible ? 1 : 0)
             | (_scintilla.ViewEol ? 2 : 0);
        set
        {
            // SCI_SETWHITESPACEVISIBLE：1 → SCWS_VISIBLEALWAYS，每个空格/Tab 都画点
            _scintilla.ViewWhitespace = (value & 1) != 0 ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
            // SCI_SETVIEWEOL：画行尾标记（Windows 下 CRLF 显示为 ←）
            _scintilla.ViewEol = (value & 2) != 0;
        }
    }

    /// <summary>
    /// 自动换行。ND2 用的是 WrapCharacter（SCI_SETWRAPMODE = SC_WRAP_CHAR，按字符断），
    /// 不是按单词断 —— 长串无空格的文本也会断开，这里照它。
    /// </summary>
    /// <remarks>读回时任何非 None 的换行模式都算「已开启」，配置只有开关两态。</remarks>
    public bool WordWrap
    {
        get => _scintilla.WrapMode != ScintillaNET.WrapMode.None;
        set => _scintilla.WrapMode = value ? ScintillaNET.WrapMode.Char : ScintillaNET.WrapMode.None;
    }

    /// <summary>当前行尾符名称（状态栏/视图菜单显示的那串文案，与 ND2 的字符串完全一致）</summary>
    public string EolName => _scintilla.EolMode switch
    {
        // 这里是精确匹配枚举值：EolMode 若被组合出别的值会落到默认分支显示 Unix(LF)
        Eol.CrLf => "Windows(CR LF)",
        Eol.Cr => "Mac(CR)",
        _ => "Unix(LF)",
    };

    /// <summary>缩进参考线的本地开关缓存（Scintilla 的 IndentationGuides 读回的是「已应用的模式」，拿不回布尔开关，所以自己记一份）</summary>
    private bool _indentGuide;

    /// <summary>
    /// 缩进参考线，照 ND2 ScintillaEditView::setIndentGuide（scintillaeditview.cpp:990-1001）：
    /// Python 用 LOOKFORWARD，其余语言用 LOOKBOTH，关掉是 NONE。
    /// ND2 换词法器会把这一项冲掉（ccnotepad.cpp:4313 的注释就是为这个才把缩进线放到装词法器之后），
    /// 所以我们也在装完词法器之后补一次。
    /// </summary>
    public bool ShowIndentGuides
    {
        get => _indentGuide;
        set { _indentGuide = value; ApplyIndentGuide(); }
    }

    /// <summary>
    /// 把 <see cref="_indentGuide"/> 开关落成 Scintilla 的 IndentationGuides 值（SCI_SETINDENTATIONGUIDES）。
    /// 词法器变化后必须重调一次，否则会被装词法器的副作用冲回默认值。
    /// </summary>
    private void ApplyIndentGuide()
    {
        // Python 的缩进即语法，只看「往下还有没有同缩进的行」，用 LookForward；
        // 其余语言上下都画（LookBoth），跟 ND2 的 if(lang == SCLEX_PYTHON) 分支一一对应
        if (!CurrentLexerName.Equals("python", StringComparison.OrdinalIgnoreCase))
            _scintilla.IndentationGuides = _indentGuide ? IndentView.LookBoth : IndentView.None;
        else
            _scintilla.IndentationGuides = _indentGuide ? IndentView.LookForward : IndentView.None;
    }

    // ==================== 字体 ====================

    /// <summary>
    /// 改编辑器字体（视图菜单「样式 1/2/3…」与全局配置都走这里）。
    /// 样式 0（STYLE_DEFAULT）是「未定义样式的兜底」，改完还要靠 STYLECLEARALL/重着色才会波及全部文字。
    /// </summary>
    /// <param name="family">字体族名（如 "Consolas"）</param>
    /// <param name="size">字号（磅），写入样式时四舍五入成整数——Scintilla 样式只吃整数字号</param>
    public void SetFont(string family, double size)
    {
        // 控件级 Font 影响 WinForms 侧测量与 DPI 换算，样式级 Font/Size 才是真正画文本用的
        var f = new System.Drawing.Font(family, (float)size);
        _scintilla.Styles[ScintillaNET.Style.Default].Font = family;
        _scintilla.Styles[ScintillaNET.Style.Default].Size = (int)Math.Round(size);
        _scintilla.Font = f;
    }

    // ==================== 查找高亮与标记 ====================

    /// <summary>高亮所有匹配项</summary>
    /// <param name="condition">查找条件</param>
    /// <returns>本次填上的区间数（同时会先清掉上一次的高亮）</returns>
    public int HighlightAllMatches(SearchCondition condition)
    {
        // 先清后画：否则改一次查找词，旧的框还留在原地
        ClearFindHighlight();
        if (condition.IsEmpty) return 0;
        return FillIndicator(FindIndicatorIndex, condition);
    }

    /// <summary>清除查找高亮（整段清掉 8 号 indicator，不碰标记色与括号高亮）</summary>
    public void ClearFindHighlight() => ClearIndicator(FindIndicatorIndex);

    /// <summary>
    /// 标记全部匹配（ND2 查找窗 Mark tab）：colorSlot 取 1..5，对应五种标记色。
    /// 标记走独立 indicator，不会把「查找」高亮一起冲掉，两者可以同时看到。
    /// 命中的位置会记进 _markRecords，供 F8 的「同词再按一次取消高亮」使用。
    /// </summary>
    /// <param name="condition">查找条件</param>
    /// <param name="colorSlot">标记色档位 1..5，越界会被夹到合法范围</param>
    /// <returns>填上的区间数；条件为空返回 0（此时也不会留下记录）</returns>
    public int MarkAll(SearchCondition condition, int colorSlot)
    {
        if (condition.IsEmpty) return 0;
        int slot = Math.Clamp(colorSlot, 1, MarkColorCount);
        // 槽位 1..5 → indicator 21..25（MarkIndicatorBase - 1 + slot）
        int count = FillIndicator(MarkIndicatorBase - 1 + slot, condition);
        // 只有真的画上东西才记一条：0 处匹配记了也是空记录，反而让 F8 误判「已标记过」
        if (count > 0)
            _markRecords.Add(new MarkRecord { Text = condition.Text, Slot = slot });
        return count;
    }

    /// <summary>标记全部匹配并给这些行加书签（ND2 的 Mark &amp; Bookmark）</summary>
    /// <param name="condition">查找条件</param>
    /// <param name="colorSlot">标记色档位 1..5</param>
    /// <returns>标记填上的区间数（书签数与它同级，但同一行的多处匹配只加一个书签）</returns>
    public int MarkAllWithBookmark(SearchCondition condition, int colorSlot)
    {
        // 标记部分与 MarkAll 完全一致（含写记录），下面只补书签
        int count = MarkAll(condition, colorSlot);
        // 一堆 MarkerAdd 包成一个撤销组，Ctrl+Z 一次撤掉整批书签
        _scintilla.BeginUndoAction();
        try
        {
            // 重新扫一遍区间：只取每处匹配的起点，映射成行号（MarkerAdd 对同一行重复加是幂等的）
            foreach (var (start, _) in FindRanges(condition))
            {
                _scintilla.Lines[_scintilla.LineFromPosition(start)].MarkerAdd(BookmarkMarkerIndex);
            }
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return count;
    }

    /// <summary>
    /// 清除全部标记色并释放标记记录，对应 ND2 的 Clear All / 工具栏「清除所有高亮」(F7)。
    /// 与 ND2 一致：这里不动书签，书签由书签菜单的 ClearAll BookMark 负责。
    /// </summary>
    public void ClearAllMarks()
    {
        // 五个 indicator 逐个整篇清（每个一次 IndicatorClearRange，比逐区间清快得多）
        for (int i = 0; i < MarkColorCount; i++)
            ClearIndicator(MarkIndicatorBase + i);
        _markRecords.Clear();
    }

    /// <summary>
    /// 只清掉某个词的标记，对应 ND2 Mark tab 的 Clear Mark
    /// （clearHighlightWord(markTextBox 里的词)）。返回清掉的区间数。
    /// </summary>
    /// <param name="text">要取消标记的词面文本（与记录里的 Text 做 Ordinal 相等比较）</param>
    /// <returns>清掉的区间总数</returns>
    public int ClearMarkOf(string text)
    {
        if (text.Length == 0) return 0;
        int cleared = 0;
        // ToList() 是为了先固定快照：循环里会往 _markRecords 做 Remove，直接迭代原集合会抛异常
        foreach (var record in _markRecords.Where(r => string.Equals(r.Text, text, StringComparison.Ordinal)).ToList())
        {
            // 用记录里的 Text 重扫（不是传入的 text），保证大小写/内容两边完全一致
            cleared += ClearIndicatorRanges(record.Slot, record.Text);
            _markRecords.Remove(record);
        }
        return cleared;
    }

    /// <summary>
    /// F8「单词高亮」（ND2 slot_wordHighlight）：有选区按选区，否则取光标处的词；
    /// 已经用当前色标记过就取消，用别的色标记过就换成当前色重标。
    /// </summary>
    /// <param name="colorSlot">当前选定的标记色档位 1..5</param>
    /// <returns>本次实际填上的区间数；取消掉时返回 0</returns>
    public int ToggleWordMark(int colorSlot)
    {
        // 跨行选区（含 \n）没法当「词」标记，退回去取光标处的词——ND2 同款处理
        string word = SelectedText;
        if (word.Length == 0 || word.Contains('\n')) word = WordAtCaret();
        if (word.Length == 0) return 0;

        // LastOrDefault：同一个词被标过多次时，最新那条才代表当前可见状态
        var record = _markRecords.LastOrDefault(r => string.Equals(r.Text, word, StringComparison.Ordinal));
        if (record != null)
        {
            // 只清这一条记录占的区间，别的词的标记要留着
            ClearIndicatorRanges(record.Slot, record.Text);
            _markRecords.Remove(record);
            // 同色 → 取消即可；异色 → 继续往下走，把它换成新色（ND2 的「换色」行为）
            if (record.Slot == colorSlot) return 0;
        }

        // ND2 走 markAllWord：区分大小写、不做全词匹配（中文全词匹配会漏）
        return MarkAll(new SearchCondition(word, SearchMode.Normal, true, false), colorSlot);
    }

    /// <summary>
    /// 清掉某一档标记色里、指定词占用的区间（定点擦，不清整个 indicator）。
    /// </summary>
    /// <param name="slot">标记色档位 1..5（越界夹取，与 MarkAll 同口径）</param>
    /// <param name="text">要擦掉的词面文本</param>
    /// <returns>实际清掉的区间数</returns>
    private int ClearIndicatorRanges(int slot, string text)
    {
        // IndicatorCurrent = SCI_SETINDICATORCURRENT，后面的 ClearRange 只作用在这一层
        var indicator = MarkIndicatorBase - 1 + Math.Clamp(slot, 1, MarkColorCount);
        _scintilla.IndicatorCurrent = indicator;
        int cleared = 0;
        // 按普通+区分大小写的条件重扫：与 MarkAll 记 Text 时的口径一致，才不会漏擦或错擦别人的格子
        foreach (var (start, end) in FindRanges(new SearchCondition(text, SearchMode.Normal, true, false)))
        {
            if (end <= start) continue;
            _scintilla.IndicatorClearRange(start, end - start);
            cleared++;
        }
        return cleared;
    }

    /// <summary>光标处（或紧邻左侧）的词</summary>
    /// <returns>词面文本；光标在文档开头、落在空白处或越界时返回空串</returns>
    public string WordAtCaret()
    {
        int caret = _scintilla.CurrentPosition;
        // caret==0 时左边没有字符可取；越过 TextLength 是异常态，一律返回空
        if (caret <= 0 || caret > _scintilla.TextLength) return string.Empty;
        // SCI_WORDSTARTPOSITION / SCI_WORDENDPOSITION 按「词字符」划边界；
        // 第二个参数 isExtend=true 表示把扩展字符集（重音字母等非 ASCII）也算作词字符，中文文档下边界更宽
        int start = _scintilla.WordStartPosition(caret, true);
        int end = _scintilla.WordEndPosition(caret, true);
        // 光标落在空白/标点之间时起止重合，视为没有词
        if (end <= start) return string.Empty;
        return _scintilla.GetTextRange(start, end - start);
    }

    /// <summary>
    /// 扫出全文所有匹配区间（含零长匹配，由调用方自己决定怎么处理）。
    /// 每次都会重设 SearchFlags 与 Target，所以可以安全地和别的查找交错调用。
    /// </summary>
    /// <param name="condition">查找条件</param>
    /// <returns>按出现顺序排列的 (Start, End) 区间；条件为空时返回空列表</returns>
    private IEnumerable<(int Start, int End)> FindRanges(SearchCondition condition)
    {
        var ranges = new List<(int, int)>();
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return ranges;

        // 默认 FindVerb.FindNext（EMPTYMATCH_ALL）：与 ND2 标记/计数的口径一致，一个位置多次匹配全收
        _scintilla.SearchFlags = condition.ToScintillaFlags();
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(needle) >= 0)
        {
            // SearchInTarget 命中后 Target 已被改写成匹配区间，直接读回即可
            int s = _scintilla.TargetStart;
            int e = _scintilla.TargetEnd;
            // TargetEnd < TargetStart 只在反向查找里出现，这里属于异常态，收工
            if (e < s) break;
            ranges.Add((s, e));
            // 末尾了就不再前进，省一次必然失败的搜索
            if (e >= _scintilla.TextLength) break;
            // 零宽匹配也要前进，否则这里会死循环
            _scintilla.TargetStart = e > s ? e : s + 1;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return ranges;
    }

    /// <summary>
    /// 把某条件的所有匹配填到指定 indicator 层上（查找高亮与标记色共用这一套）。
    /// </summary>
    /// <param name="indicatorIndex">indicator 编号（8 = 查找高亮，21..25 = 标记色）</param>
    /// <param name="condition">查找条件</param>
    /// <returns>实际填上的区间数（零长匹配不计入，因为它画不出任何东西）</returns>
    private int FillIndicator(int indicatorIndex, SearchCondition condition)
    {
        int count = 0;
        // 先设当前层，IndicatorFillRange(pos, length) = SCI_INDICATORFILLRANGE 只作用在 IndicatorCurrent 上
        _scintilla.IndicatorCurrent = indicatorIndex;
        foreach (var (start, end) in FindRanges(condition))
        {
            if (end <= start) continue;
            _scintilla.IndicatorFillRange(start, end - start);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 整篇清掉某一层 indicator（SCI_INDICATORCLEARRANGE 覆盖 [0, 全文]）。
    /// 只动传入的这一层，别的 indicator 内容不受影响。
    /// </summary>
    /// <param name="indicatorIndex">要清空的 indicator 编号</param>
    private void ClearIndicator(int indicatorIndex)
    {
        _scintilla.IndicatorCurrent = indicatorIndex;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);
    }
}
