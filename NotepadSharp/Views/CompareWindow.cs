using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotepadSharp.Controls;
using NotepadSharp.Services;
// 以下别名用于消除同名二义性：Button / CheckBox / Orientation / MessageBox / Application 在
// WPF 与 WinForms（以及 System.Windows 与 System.Windows.Controls）之间重名，不指向具体类型就编译不过；
// WpfAlignment 是刻意改名——本文件还要直接用 VerticalAlignment，两者都叫 Alignment 会混
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using WpfAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Views;

/// <summary>
/// 文件对比窗口：左右两个只读编辑器按行对齐显示差异（行级 LCS 自行实现），
/// 差异行整行着色，支持上一个/下一个差异跳转。
/// 本窗口只做「取文本 → 切行 → 调差异引擎 → 把对齐结果铺成左右两份等长文本 → 逐行上色」，
/// 前后缀裁剪与 LCS 配对全在 <see cref="LineDiff"/> 里，窗口本身不含算法。
/// 备注：ND2 开源版的对比引擎是空壳（商业代码未随源码发布），这里是按行为规格自研重建，
/// 因此不存在可对应的 ND2 源文件行号。
/// </summary>
public sealed class CompareWindow : Window
{
    /// <summary>左侧（原文）编辑器面板；只读，仅用来展示对齐后的左列</summary>
    private readonly EditorControl _left = new();
    /// <summary>右侧（对比文）编辑器面板；与左侧行数恒等，所以同一个行号在两边指同一「对齐坐标」</summary>
    private readonly EditorControl _right = new();
    /// <summary>左编辑器顶部的标题行，显示左边的文件路径或「编辑区内容」</summary>
    private readonly TextBlock _leftLabel = new();
    /// <summary>右编辑器顶部的标题行，语义同 <see cref="_leftLabel"/></summary>
    private readonly TextBlock _rightLabel = new();
    /// <summary>窗口底部的状态栏：提示下一步操作，并给出差异段数 / 不同行数等统计</summary>
    private readonly TextBlock _status = new();
    /// <summary>「忽略大小写」开关：比较键统一转小写；勾选状态一变就整体重算差异</summary>
    private readonly CheckBox _ignoreCase = new() { Content = "忽略大小写" };
    /// <summary>「忽略行尾空白」开关：只 TrimEnd，行首缩进变化仍算真实改动</summary>
    private readonly CheckBox _ignoreTrivia = new() { Content = "忽略行尾空白" };

    /// <summary>左侧来源文件路径；空串表示来源是「当前编辑区」而不是磁盘文件（见 Describe）</summary>
    private string _pathA = string.Empty;
    /// <summary>右侧来源文件路径；语义同 <see cref="_pathA"/></summary>
    private string _pathB = string.Empty;
    /// <summary>左侧原文按换行切好的行数组；_diff 里的 AIndex 就是它的 0 基下标</summary>
    private string[] _linesA = Array.Empty<string>();
    /// <summary>右侧原文切行结果；_diff 里的 BIndex 指回它</summary>
    private string[] _linesB = Array.Empty<string>();
    /// <summary>最近一次算出的对齐序列；它的长度等于左右两份显示文本各自的行数</summary>
    private List<DiffLine> _diff = new();
    /// <summary>各差异段在对齐序列里的首行下标，供「上一个/下一个差异」跳转</summary>
    private List<int> _hunks = new();
    /// <summary>当前停留的差异段序号（0 基）；-1 表示还没跳过任何一段，下一次 +1 即跳到第一段</summary>
    private int _hunkCursor = -1;

    /// <summary>
    /// 让用户在编辑器标签页里打开某个文件：由主窗口注入（见 MainWindow.EnsureCompareWindow），
    /// 对应工具栏「在编辑器打开左 / 在编辑器打开右」两个按钮。为 null 时这两个按钮静默无效。
    /// </summary>
    public Action<string>? OpenFileRequested { get; set; }

    /// <summary>
    /// 构造对比窗口：只搭 UI、装事件，不读任何文件；两侧内容为空时状态栏提示先选文件。
    /// 真正填内容的入口是 <see cref="SetLeft"/> / <see cref="SetRight"/>（工具栏选文件也走它们）。
    /// </summary>
    public CompareWindow()
    {
        // ===== 窗口外观：固定 1200x700、屏幕居中；底色取主题字典里的面板色 =====
        // as 转换失败时得到 null，WPF 会退回默认底色，所以这里不需要额外兜底
        Title = "文件对比";
        Width = 1200;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;

        // ===== 骨架：DockPanel 三段——顶部工具栏、底部状态栏、剩下的中间区放左右两栏 =====
        // 先 Dock 顶/底再 Add，中间未 Dock 的子元素（下面的 grid）自动填满剩余空间
        var root = new DockPanel { Margin = new Thickness(6) };
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);

        // ===== 中间区：左星列 | 1px 列 | 右星列，中间列放 GridSplitter 供拖动改变左右宽度 =====
        // 两侧都用 Star 保证初始等宽；分隔条单独占一列，不参与内容布局
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = WpfAlignment.Center,
            Background = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush,
        };
        Grid.SetColumn(splitter, 1);
        // Pane 把「标题 + 编辑器」包成一个 DockPanel 再塞进指定列；左右分别占第 0、2 列
        grid.Children.Add(Pane(_left, _leftLabel, "左", 0));
        grid.Children.Add(splitter);
        grid.Children.Add(Pane(_right, _rightLabel, "右", 2));
        root.Children.Add(grid);

        Content = root;

        // ===== 两侧编辑器统一设置 =====
        // ReadOnly：对比窗口只做展示，改动应该回到主窗口的标签页里做
        // WordWrap=false 是对齐的前提——一旦换行，左右同一条逻辑行渲染出的行数不同，
        // 「同一个行号指同一处」的同步语义就崩了
        // ConfigureDiffMarkers 只需配置一次（底色槽位与文档无关），这里先声明好
        foreach (var editor in new[] { _left, _right })
        {
            editor.ReadOnly = true;
            editor.WordWrap = false;
            editor.ConfigureDiffMarkers();
        }

        // 两个比较规则开关任一变化都要整体重算：差异是「归一化比较键」的函数，口径变了必须重跑 LCS
        _ignoreCase.Checked += (_, _) => Rebuild();
        _ignoreCase.Unchecked += (_, _) => Rebuild();
        _ignoreTrivia.Checked += (_, _) => Rebuild();
        _ignoreTrivia.Unchecked += (_, _) => Rebuild();

        // F7 跳下一个差异、Shift+F7 跳上一个。用 PreviewKeyDown（隧道事件）而不是 KeyDown：
        // 焦点在 Scintilla 里时按键会先被原生控件吞掉，预览阶段拦下来才可靠
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F7) { MoveHunk(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        };

        UpdateStatus("选择左右两侧文件后开始对比。");
    }

    /// <summary>
    /// 构建顶部工具栏：一排操作按钮 + 两个比较规则复选框，全部水平排列。
    /// </summary>
    /// <returns>可直接塞进 DockPanel 顶部的横向 StackPanel</returns>
    private StackPanel BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

        // 局部工厂：批量造「点一下就执行 action」的小按钮，省掉每个按钮重复写 Click 订阅
        // （label 同时作为按钮文字，action 为点击后要跑的委托）
        Button Text(string label, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => action();
            return b;
        }

        // ===== 按钮组：选文件 → 重算 → 跳转 → 把对比对象送回主编辑器 =====
        // 选文件用「选了才 Set」的写法：取消对话框时保持原内容，不会把已加载的一侧清空
        panel.Children.Add(Text("选择左侧…", () => { if (PickFile(out var p)) SetLeftFromFile(p); }));
        panel.Children.Add(Text("选择右侧…", () => { if (PickFile(out var p)) SetRightFromFile(p); }));
        panel.Children.Add(Text("重新对比", Rebuild));
        panel.Children.Add(Text("上一个差异", () => MoveHunk(-1)));
        panel.Children.Add(Text("下一个差异", () => MoveHunk(1)));
        // 「在编辑器打开」只在已经填过内容时有效：路径为空串表示来源是编辑区，没意义，直接跳过
        // OpenFileRequested 用 ?. 调用：宿主没注入回调时静默不响应，不抛空引用
        panel.Children.Add(Text("在编辑器打开左", () => { if (_pathA.Length > 0) OpenFileRequested?.Invoke(_pathA); }));
        panel.Children.Add(Text("在编辑器打开右", () => { if (_pathB.Length > 0) OpenFileRequested?.Invoke(_pathB); }));

        // ===== 比较规则开关：和按钮拉开一点间距，垂直居中对齐，文字用主题前景色 =====
        _ignoreCase.Margin = new Thickness(12, 0, 8, 0);
        _ignoreTrivia.Margin = new Thickness(0, 0, 8, 0);
        foreach (var box in new[] { _ignoreCase, _ignoreTrivia })
        {
            box.VerticalAlignment = VerticalAlignment.Center;
            box.Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;
            panel.Children.Add(box);
        }
        return panel;
    }

    /// <summary>
    /// 把「标题行 + 编辑器」组合成一个面板并登记到 Grid 的指定列（标题在上、编辑器填满其余空间）。
    /// </summary>
    /// <param name="editor">该列使用的编辑器控件</param>
    /// <param name="label">该列顶部的标题文本控件（后续由 SetLeft/SetRight 改写它）</param>
    /// <param name="title">标题前缀，"左" 或 "右"；未选择文件时后缀固定是「（未选择）」</param>
    /// <param name="column">要落进的 Grid 列下标（0 = 左列，2 = 右列）</param>
    /// <returns>已设好 Grid.Column 的 DockPanel，可直接加入父容器</returns>
    private UIElement Pane(EditorControl editor, TextBlock label, string title, int column)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        // 初始占位文案；真正加载文件后由 SetLeft/SetRight 覆盖成完整路径
        label.Text = title + "：（未选择）";
        label.Margin = new Thickness(2, 0, 2, 2);
        label.Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush;
        DockPanel.SetDock(label, Dock.Top);
        dock.Children.Add(label);
        // 编辑器不 Dock，作为最后一个子元素自动吃掉剩余高度
        dock.Children.Add(editor);
        Grid.SetColumn(dock, column);
        return dock;
    }

    /// <summary>
    /// 弹一个「打开文件」对话框（限定在本窗口模态，带 Owner 保证置顶）。
    /// </summary>
    /// <param name="path">出参：用户选中的文件完整路径；取消时为空串</param>
    /// <returns>true = 用户确认选择；false = 取消或关闭对话框，调用方应保持现状</returns>
    private bool PickFile(out string path)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择对比文件", CheckFileExists = true };
        if (dlg.ShowDialog(this) == true)
        {
            path = dlg.FileName;
            return true;
        }
        path = string.Empty;
        return false;
    }

    /// <summary>工具栏「选择左侧」用：自己读文件（含编码嗅探）后填入左侧槽位</summary>
    /// <param name="path">要作为左侧原文的文件路径</param>
    public void SetLeftFromFile(string path) => SetLeft(path, ReadText(path, out _));

    /// <summary>工具栏「选择右侧」用：自己读文件后填入右侧槽位</summary>
    /// <param name="path">要作为右侧对比文的文件路径</param>
    public void SetRightFromFile(string path) => SetRight(path, ReadText(path, out _));

    /// <summary>把内容放进对比槽位；path 可为空（例如「当前编辑区」）</summary>
    /// <param name="path">来源文件路径；空串表示内容来自主窗口编辑区，不做语法高亮</param>
    /// <param name="content">该侧的完整文本（含未保存改动也可以），内部会自行切行</param>
    public void SetLeft(string path, string content)
    {
        // 只记录来源与切行结果，真正的对比交给 Rebuild：这样每填一侧就立刻刷新观感
        _pathA = path;
        _linesA = SplitLines(content);
        _leftLabel.Text = "左：" + Describe(path);
        Rebuild();
    }

    /// <summary>右侧槽位的对应版本；语义与 <see cref="SetLeft"/> 完全一致</summary>
    /// <param name="path">来源文件路径；空串表示内容来自编辑区</param>
    /// <param name="content">右侧完整文本</param>
    public void SetRight(string path, string content)
    {
        _pathB = path;
        _linesB = SplitLines(content);
        _rightLabel.Text = "右：" + Describe(path);
        Rebuild();
    }

    /// <summary>
    /// 读文件并解码成文本：整篇读入字节后交给 <see cref="EncodingService"/> 探测代码页再解码。
    /// 这里刻意不做 try/catch——文件被占用/无权限等异常要冒泡给调用方（主窗口那边统一弹框提示）。
    /// </summary>
    /// <param name="path">要读取的文件路径</param>
    /// <param name="codePage">出参：探测到的代码页；对比只看文本用不上，调用方一般传 <c>out _</c> 丢弃</param>
    /// <returns>已去掉 BOM 的纯文本内容</returns>
    private static string ReadText(string path, out int codePage)
    {
        byte[] bytes = File.ReadAllBytes(path);
        // Detect 返回 (代码页, 是否有 BOM)：bom 这里不消费（对比只看解码后的文本），
        // 解构写成两段只是为了取第一个值。解码统一 stripBom 把 BOM 剥掉，
        // 否则一侧带 BOM 会让第一行凭空多出不可见字符，产生假的「修改」差异
        (codePage, var bom) = EncodingService.Detect(bytes);
        return EncodingService.Decode(bytes, codePage, stripBom: true);
    }

    /// <summary>
    /// 统一切行：先把 CRLF 与 CR 归一成 LF 再 Split，使两侧换行风格不同（Windows/Unix/Mac）
    /// 不会造成假差异；行尾符本身也就不参与后续比较。
    /// </summary>
    /// <param name="content">整篇文本</param>
    /// <returns>行数组；文本以换行结尾时末尾会多出一个空串（保持行数与编辑器行号一致）</returns>
    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// 生成标题行文案：有路径就显示「文件名（完整路径）」，空路径说明内容是编辑区里的未保存文本。
    /// </summary>
    /// <param name="path">来源路径，可能为空串</param>
    /// <returns>可直接显示的说明文字</returns>
    private static string Describe(string path)
        => path.Length == 0 ? "编辑区内容" : $"{Path.GetFileName(path)}（{path}）";

    /// <summary>
    /// 重算并刷新：算对齐序列 → 铺成左右两份等长文本 → 灌入编辑器 → 逐行打差异底色 → 更新统计。
    /// 任何入口（选文件、设内容、改比较规则、点「重新对比」）最终都收敛到这里。
    /// 全程同步执行，文本级对比够用；超大文件会卡 UI，是已知的取舍。
    /// </summary>
    private void Rebuild()
    {
        // 前置校验：两侧都得有内容。判据是「路径和行数组同时为空」——
        // 路径为空但行数组非空，说明内容来自主窗口编辑区（「与磁盘版本对比」就是这种），同样算就绪
        if (_pathA.Length == 0 && _linesA.Length == 0)
        {
            UpdateStatus("请先选择左侧文件。");
            return;
        }
        if (_pathB.Length == 0 && _linesB.Length == 0)
        {
            UpdateStatus("请先选择右侧文件。");
            return;
        }

        // ===== 差异计算 =====
        // Compute 返回的是「并排显示顺序」的行序列，每行带 Kind/AIndex/BIndex；
        // AIndex、BIndex 指回各自原文的行号，-1 表示这一侧在该位置是占位空行
        _diff = LineDiff.Compute(_linesA, _linesB, _ignoreCase.IsChecked == true, _ignoreTrivia.IsChecked == true);
        // 把连续的非 Equal 行折叠成「段」，只留每段首行的下标，用于上一处/下一处跳转
        _hunks = LineDiff.HunkStarts(_diff);
        // 重算后段数可能变化，旧游标失去意义：退回 -1「还没跳过」，下一次 F7 自然落到第一段
        _hunkCursor = -1;

        // ===== 把对齐序列拆成左右两列显示内容 =====
        // textA/textB 是两列的逐行文本；marksA/marksB 记每行要用的底色槽位，0 表示不上色
        // 容量按 _diff.Count 预分配：对齐后行数等于 _diff 行数，一次到位不扩容
        var textA = new List<string>(_diff.Count);
        var textB = new List<string>(_diff.Count);
        var marksA = new List<int>(_diff.Count);
        var marksB = new List<int>(_diff.Count);

        foreach (var d in _diff)
        {
            // 占位行的关键一步：AIndex/BIndex 为 -1 的那一侧补一个空串，
            // 使左右两份文本行数恒等、下标一一对应——这正是「同一个行号能同时跳两边」的前提，
            // 不需要额外的行映射表
            textA.Add(d.AIndex >= 0 ? _linesA[d.AIndex] : string.Empty);
            textB.Add(d.BIndex >= 0 ? _linesB[d.BIndex] : string.Empty);
            int markA = 0, markB = 0;
            // 差异类型 → 底色：Change 两侧都标黄；Delete 只左列标橙（右列留白占位）；
            // Insert 只右列标绿（左列留白占位）；Equal 两边 mark 都是 0，不上色
            switch (d.Kind)
            {
                case DiffKind.Change: markA = markB = EditorControl.ModifyMarkerIndex; break;
                case DiffKind.Delete: markA = EditorControl.DeleteMarkerIndex; break;
                case DiffKind.Insert: markB = EditorControl.WriteMarkerIndex; break;
            }
            marksA.Add(markA);
            marksB.Add(markB);
        }

        // ===== 灌文本 + 上色：顺序不能颠倒 =====
        // Load 内部是 ClearAll + AddText，会把整篇文档（含旧 marker）推倒重建，
        // 所以必须先生成新文档，再按行号补底色，否则上一次的残留色会盖在新一轮结果上
        Load(_left, textA, _pathA);
        Load(_right, textB, _pathB);
        // mark == 0 是「相同行」的哨兵，跳过不调用；行号直接用对齐下标 i，左右通用
        for (int i = 0; i < marksA.Count; i++) if (marksA[i] != 0) _left.MarkDiffLine(i, marksA[i]);
        for (int i = 0; i < marksB.Count; i++) if (marksB[i] != 0) _right.MarkDiffLine(i, marksB[i]);

        // ===== 统计并写状态栏：段数按折叠后的 hunk 计，行数按未折叠的非 Equal 行计 =====
        int changed = _diff.Count(d => d.Kind != DiffKind.Equal);
        UpdateStatus($"{_hunks.Count} 处差异段，{changed} 行不同 · 左 {_linesA.Length} 行 / 右 {_linesB.Length} 行（F7 下一个，Shift+F7 上一个）");
    }

    /// <summary>
    /// 把一个对齐列写入编辑器：整篇替换文本 → 按来源路径配词法 → 套主题 → 声明差异底色槽位。
    /// 顺序有讲究：<see cref="EditorControl.LoadContent"/> 内部是 ClearAll + AddText，
    /// 会连带清掉上一轮的所有 marker，所以词法、主题、底色槽位都要在灌文本之后重设，
    /// 逐行底色则更晚一步由 Rebuild 补上。
    /// </summary>
    /// <param name="editor">目标编辑器（左或右）</param>
    /// <param name="lines">该列的逐行文本，行间用 \n 拼回一整篇</param>
    /// <param name="path">来源文件路径；空串（编辑区来源）时跳过语法高亮，保持纯文本词法</param>
    private void Load(EditorControl editor, List<string> lines, string path)
    {
        editor.LoadContent(string.Join("\n", lines));
        if (path.Length > 0) editor.SetLexerForFile(path);
        editor.ApplyTheme();
        editor.ConfigureDiffMarkers();
    }

    /// <summary>
    /// 把左右两侧同时定位到相邻的差异段首行，并刷新「第 N / M 处」提示。
    /// 行号在左右两边通用（占位空行已保证两份文本行数恒等），所以一次跳转两侧同步。
    /// </summary>
    /// <param name="delta">段序号增量：+1 去下一段，-1 回上一段；越界由 Math.Clamp 夹住，不循环</param>
    private void MoveHunk(int delta)
    {
        if (_hunks.Count == 0) return;
        // 夹在 [0, 段数-1]：注意 clamp 不环绕，且当前游标为 -1 时按 -1 也会被夹到 0，
        // 也就是在「还没跳过」的状态下按 Shift+F7 同样落到第一段
        _hunkCursor = Math.Clamp(_hunkCursor + delta, 0, _hunks.Count - 1);
        int line = _hunks[_hunkCursor];
        _left.GoToLine(line);
        _right.GoToLine(line);
        // 用户看的是 1 基行号，状态栏这里现场 +1；内部行号始终是 0 基
        UpdateStatus($"差异 {_hunkCursor + 1}/{_hunks.Count}，从第 {line + 1} 行开始");
    }

    /// <summary>
    /// 写底部状态栏：每次都重设边距与前景色（强调色），保证主题切换或首次赋值后观感一致。
    /// </summary>
    /// <param name="message">要显示的提示或统计文案</param>
    private void UpdateStatus(string message)
    {
        _status.Text = message;
        _status.Margin = new Thickness(2, 4, 2, 0);
        _status.Foreground = Application.Current.Resources["AccentBrush"] as System.Windows.Media.Brush;
    }
}
