using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// 本项目同时引用了 WPF 与 WinForms（浏览文件夹用了 System.Windows.Forms.FolderBrowserDialog），
// Application / Button / CheckBox / Orientation / MessageBox / ListView / TextBox / Brush 这些名字在两边都有，
// 不显式指向 WPF 版本会报二义性；WpfAlignment 干脆改名，避免和 VerticalAlignment 混读
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListView = System.Windows.Controls.ListView;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using WpfAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Views;

/// <summary>
/// 目录对比窗口：递归比对两个目录里的同名文件（大小 + 内容），列出仅左侧 / 仅右侧 / 内容不同。
/// 目录模式下双击两边都有的文件，直接进左右对比。
/// 流程分三步：递归索引两个目录（相对路径 → 全路径字典）→ 以左为主做并集比对
/// （先比文件大小，大小相同才逐字节比内容）→ 结果排序后灌进 GridView。
/// 扫描与比对在后台线程跑，可随时「停止」；UI 只在结果回来后更新。
/// 备注：ND2 开源版的对比（含目录对比）是空壳，本实现按行为规格自研，无可对应的 ND2 行号。
/// </summary>
public sealed class DirCompareWindow : Window
{
    /// <summary>左目录输入框；可直接粘贴路径，也支持「浏览…」选文件夹</summary>
    private readonly TextBox _leftDir = new() { Text = string.Empty };
    /// <summary>右目录输入框；语义同 <see cref="_leftDir"/></summary>
    private readonly TextBox _rightDir = new() { Text = string.Empty };
    /// <summary>结果表：GridView 四列（相对路径 / 状态 / 左大小 / 右大小），数据源是 <see cref="_entries"/> 的筛选视图</summary>
    private readonly ListView _list = new();
    /// <summary>底部状态栏：进度提示与「仅左 / 仅右 / 不同 / 相同」计数</summary>
    private readonly TextBlock _status = new();
    /// <summary>「只显示差异」开关，默认勾选；切换只重筛视图，不会重跑对比</summary>
    private readonly CheckBox _onlyDiff = new() { Content = "只显示差异", IsChecked = true };
    /// <summary>最近一次对比的全量结果（含「相同」项）；<see cref="RefreshView"/> 按开关筛它</summary>
    private readonly List<DirEntry> _entries = new();
    /// <summary>后台扫描的取消令牌源；点「停止」或关窗口时置为取消。null 表示当前没有在跑的对比</summary>
    private CancellationTokenSource? _cts;

    /// <summary>只在单侧存在的文件被双击时，用主窗口编辑器打开它（由 MainWindow 注入）</summary>
    /// <remarks>宿主没赋值时保持 null，此时双击静默无响应。</remarks>
    public Action<string>? OpenFileRequested { get; set; }
    /// <summary>两侧都存在且内容有差异的行被双击时，把这一对路径送进文件对比窗口（由 MainWindow 注入）</summary>
    public Action<string, string>? CompareFilesRequested { get; set; }

    /// <summary>
    /// 构造目录对比窗口：搭 UI + 接线，不访问文件系统；真正的扫描要等用户点「开始对比」。
    /// </summary>
    public DirCompareWindow()
    {
        // ===== 窗口外观：1000x640、居中，底色取主题面板色 =====
        Title = "目录对比";
        Width = 1000;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        // ===== 骨架：顶部工具栏 + 中间结果表 + 底部状态栏 =====
        // root 未设 LastChildFill 之外的 Dock，先 Dock 顶/底，后加的 _list 自动填满中间
        var root = new DockPanel { Margin = new Thickness(8) };

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);

        BuildList();
        root.Children.Add(_list);
        Content = root;

        // ===== 事件接线 =====
        // 勾/去勾「只显示差异」只重筛现成结果，代价低，所以不必重跑对比
        _onlyDiff.Checked += (_, _) => RefreshView();
        _onlyDiff.Unchecked += (_, _) => RefreshView();
        // 关窗口时必须掐掉后台扫描：否则 Task.Run 里的递归索引会拿着已释放的窗口状态继续跑
        Closed += (_, _) => _cts?.Cancel();
        UpdateStatus("填两个目录（可以直接粘贴路径），然后点「开始对比」；双击结果行可打开差异。");
    }

    /// <summary>
    /// 预填两个目录路径（主窗口「目录对比」菜单会用当前文档所在目录调它）。
    /// 只填文本框，不自动开始对比，避免误触发一次全盘扫描。
    /// </summary>
    /// <param name="left">左目录路径，原样写入</param>
    /// <param name="right">右目录路径；null 或空串时保留右框现状</param>
    public void SetDirectories(string left, string? right = null)
    {
        _leftDir.Text = left;
        // 用 IsNullOrEmpty 而不是 == null：调用方可能传空串表示「只填左边一侧」
        if (!string.IsNullOrEmpty(right)) _rightDir.Text = right;
    }

    /// <summary>
    /// 构建工具栏，两行：第一行放左右目录输入框与两个「浏览…」，第二行放操作按钮与筛选开关。
    /// 顺带在这里把状态栏的配色与 AutomationId 也设好（测试脚本靠 AutomationId 定位控件）。
    /// </summary>
    /// <returns>可直接 Dock 到顶部的 StackPanel</returns>
    private UIElement BuildToolbar()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        // ===== 第一行：左目录 | 浏览… | 右目录 | 浏览… =====
        // 两个输入框定宽 330，靠 Gap 撑开间距，保证两行按钮视觉上不错位
        // AutomationId 给 UI 自动化测试用（DirA / DirB），与可见文字解耦
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row1.Children.Add(Caption("左目录"));
        _leftDir.Width = 330;
        _leftDir.Padding = new Thickness(4, 2, 4, 2);
        AutomationProperties.SetAutomationId(_leftDir, "DirA");
        row1.Children.Add(_leftDir);
        row1.Children.Add(Gap(6));
        row1.Children.Add(TextButton("浏览…", () => BrowseInto(_leftDir)));
        row1.Children.Add(Gap(18));

        row1.Children.Add(Caption("右目录"));
        _rightDir.Width = 330;
        _rightDir.Padding = new Thickness(4, 2, 4, 2);
        AutomationProperties.SetAutomationId(_rightDir, "DirB");
        row1.Children.Add(_rightDir);
        row1.Children.Add(Gap(6));
        row1.Children.Add(TextButton("浏览…", () => BrowseInto(_rightDir)));

        // ===== 第二行：开始 / 停止 / 交换 / 打开选中 + 只显示差异 =====
        // 「开始对比」是 async void 语义，这里用 `_ = ` 显式丢弃 Task，
        // 表示有意不 await（异常在 RunCompareAsync 内部自己 catch），也压掉 CS4014 警告
        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(TextButton("开始对比", () => _ = RunCompareAsync()));
        row2.Children.Add(TextButton("停止", () => _cts?.Cancel()));
        row2.Children.Add(TextButton("交换左右", Swap));
        row2.Children.Add(TextButton("打开选中", OpenSelected));
        _onlyDiff.Margin = new Thickness(12, 0, 0, 0);
        _onlyDiff.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        _onlyDiff.Foreground = Application.Current.Resources["TextBrush"] as Brush;
        row2.Children.Add(_onlyDiff);

        panel.Children.Add(row1);
        panel.Children.Add(row2);
        // 状态栏虽然不在工具栏里（它 Dock 在窗口底部），但配色与 AutomationId 顺手在这里一并设好
        _status.Foreground = Application.Current.Resources["TextSecondaryBrush"] as Brush;
        _status.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetAutomationId(_status, "DirStatus");
        return panel;
    }

    /// <summary>
    /// 初始化结果表：定义四列视图、套主题色、把双击接到「打开选中」。
    /// 这里只配 UI，不设 ItemsSource——空表由 <see cref="RefreshView"/> 首次赋值。
    /// </summary>
    private void BuildList()
    {
        var view = new GridView();
        view.Columns.Add(Column("相对路径", 430));
        view.Columns.Add(Column("状态", 110));
        view.Columns.Add(Column("左大小", 100));
        view.Columns.Add(Column("右大小", 100));
        _list.View = view;
        _list.Margin = new Thickness(0, 6, 0, 0);
        _list.BorderBrush = Application.Current.Resources["BorderBrush"] as Brush;
        _list.Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        _list.Foreground = Application.Current.Resources["TextBrush"] as Brush;
        AutomationProperties.SetAutomationId(_list, "DirList");
        // 双击即「打开选中」：单侧文件走编辑器，双侧文件走逐行对比，分流在 OpenSelected 里判
        _list.MouseDoubleClick += (_, _) => OpenSelected();
    }

    /// <summary>
    /// 造一个 GridView 列：表头文字同时决定绑定到 <see cref="DirEntry"/> 的哪个属性。
    /// </summary>
    /// <param name="header">表头显示文字，必须是「相对路径 / 状态 / 左大小 / 右大小」之一</param>
    /// <param name="width">列宽（像素）</param>
    /// <returns>已带好 DisplayMemberBinding 的列</returns>
    /// <remarks>未匹配到的表头落到默认分支绑 RightSizeText——新增列时记得同步这个 switch。</remarks>
    private static GridViewColumn Column(string header, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new System.Windows.Data.Binding(header switch
        {
            "相对路径" => nameof(DirEntry.RelativePath),
            "状态" => nameof(DirEntry.StatusText),
            "左大小" => nameof(DirEntry.LeftSizeText),
            _ => nameof(DirEntry.RightSizeText),
        }),
    };

    /// <summary>造一个与输入框同行居中的小标签（"左目录" / "右目录"）。</summary>
    /// <param name="text">标签文字</param>
    /// <returns>可直接加入横向 StackPanel 的 TextBlock</returns>
    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    /// <summary>造一段透明占位宽度，用来拉开按钮/控件之间的水平间距（StackPanel 没有 gap 概念）。</summary>
    /// <param name="width">占位宽度（像素）</param>
    /// <returns>无内容的 Border，只占位不响应鼠标</returns>
    private static FrameworkElement Gap(double width) => new Border { Width = width };

    /// <summary>
    /// 造一个「点一下就执行 action」的文字按钮，统一内边距与右间距。
    /// </summary>
    /// <param name="label">按钮文字</param>
    /// <param name="action">点击后要执行的委托</param>
    /// <returns>已订阅 Click 的按钮</returns>
    private Button TextButton(string label, Action action)
    {
        var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// 弹 WinForms 的文件夹选择框，把选中的路径写进目标输入框。
    /// 选 WinForms 而不是自己写，是因为 WPF 原生没有「选文件夹」对话框，而它支持树形浏览、更贴近用户习惯。
    /// </summary>
    /// <param name="target">要回填的输入框（左或右）</param>
    private void BrowseInto(TextBox target)
    {
        // using：对话框是非托管句柄包装的 IDisposable，关掉即释放
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        // 框里已有合法路径时把它作为初始位置，省得用户从头翻盘；路径失效（手输错了）就忽略，用默认位置
        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
            dlg.SelectedPath = target.Text;
        // 取消时不改动输入框，保留用户原来填的内容
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    /// <summary>
    /// 交换左右：两个输入框的文字互换，已算出的结果也整体左右对调，省掉一次重新扫描。
    /// 注意结果只是显示层面的对调（路径、大小、仅左/仅右状态互换），Same/Different 不受影响。
    /// </summary>
    private void Swap()
    {
        (_leftDir.Text, _rightDir.Text) = (_rightDir.Text, _leftDir.Text);
        foreach (var e in _entries) e.Swap();
        // 交换不改变差异数量与筛选条件，但仍要重设 ItemsSource 让 GridView 重新取值
        RefreshView();
    }

    /// <summary>
    /// 「开始对比」入口：校验输入 → 起后台线程扫描比对 → 回 UI 线程刷结果。
    /// 由按钮以 fire-and-forget 方式触发（调用处 `_ = ` 丢弃返回的 Task），
    /// 所有异常在这里就地转成状态栏文字，不外抛。
    /// </summary>
    /// <returns>Task 表示对比流程；await 到的是后台任务完成，不是结果（结果直接落到 <see cref="_entries"/>）</returns>
    private async Task RunCompareAsync()
    {
        // ===== 输入校验：两个目录都得存在，且不是同一个 =====
        // Trim 是因为用户常从资源管理器地址栏粘贴，带首尾空格；全路径比较用 OrdinalIgnoreCase
        // （Windows 文件系统大小写不敏感，且不该受当前区域设置影响）
        string a = _leftDir.Text.Trim(), b = _rightDir.Text.Trim();
        if (!Directory.Exists(a) || !Directory.Exists(b))
        {
            MessageBox.Show(this, "左右两个目录都要存在。", "目录对比", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 同目录自比没有意义（结果全「相同」且可能索引循环），TrimEnd('\\') 让 "C:\x" 与 "C:\x\" 判为同一个
        if (string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "左右是同一个目录。", "目录对比", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ===== 取消旧任务再起新的：连点两次「开始对比」不能让两个扫描并存互相写结果 =====
        // 旧的 CancellationTokenSource 只 Cancel 不 Dispose：后台线程可能还在读它的 Token，
        // 提前释放会让它抛 ObjectDisposedException
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        UpdateStatus("正在扫描…");

        // ===== 后台执行，UI 不冻结；结果用局部变量接住，成功才落到 _entries =====
        List<DirEntry> result;
        try
        {
            // Task.Run 把纯 IO 的递归扫描丢到线程池；传 token 只是「不排队」的调度取消，
            // 真正的中途停止靠 CompareDirectories 内部 ThrowIfCancellationRequested
            result = await Task.Run(() => CompareDirectories(a, b, token), token);
        }
        catch (OperationCanceledException)
        {
            // 点「停止」或关窗口：这是正常路径，只提示，不当作错误
            UpdateStatus("已停止。");
            return;
        }
        catch (Exception ex)
        {
            // 兜住无权限子目录（如 System Volume Information）、扫描途中文件被删等 IO 异常：
            // 目前整个对比会一次性失败，不做部分结果——宁可报错也不要给出误导性的「已比完」
            UpdateStatus("对比失败：" + ex.Message);
            return;
        }

        // 回到 UI 线程（await 续体捕获了上下文），可以安全地动 _entries 与 ListView
        _entries.Clear();
        _entries.AddRange(result);
        RefreshView();
    }

    /// <summary>
    /// 核心比对（在后台线程执行）：索引两侧目录 → 以左为主逐个判状态 → 补上「仅右侧」→ 按路径排序。
    /// 状态判定口径：同名且大小相同才去逐字节比内容（短路省 IO），大小不同直接判 Different。
    /// </summary>
    /// <param name="left">左目录绝对路径</param>
    /// <param name="right">右目录绝对路径</param>
    /// <param name="token">取消令牌；在左侧遍历的每个文件处检查一次，使「停止」能在秒级生效</param>
    /// <returns>排好序的全量结果（含「相同」项，是否显示由 <see cref="RefreshView"/> 决定）</returns>
    private static List<DirEntry> CompareDirectories(string left, string right, CancellationToken token)
    {
        // 两次递归索引各建一张「相对路径 → 全路径」表；相对路径作 key，两边才能同名对齐
        var leftFiles = Index(left);
        var rightFiles = Index(right);
        var list = new List<DirEntry>();

        // ===== 第一趟：遍历左侧，产出「仅左侧」与「相同 / 内容不同」 =====
        foreach (var (rel, leftPath) in leftFiles)
        {
            // 取消检查点：每个文件一次。目录可能几十万项，粒度足够，且不会像每字节检查那样拖慢
            token.ThrowIfCancellationRequested();
            // 右表没这个相对路径 → 左侧独有；右侧大小填 null，界面上显示成「—」
            if (!rightFiles.TryGetValue(rel, out var rightPath))
            {
                list.Add(new DirEntry(rel, DirStatus.OnlyLeft, new FileInfo(leftPath).Length, null, leftPath, null));
                continue;
            }
            // 两侧都有：先看属性里的文件大小（一次 stat，几乎零成本），
            // 大小不等直接判 Different，短路掉逐字节读盘；只有大小相等才真去比内容
            var li = new FileInfo(leftPath);
            var ri = new FileInfo(rightPath);
            var status = li.Length != ri.Length || !SameContent(leftPath, rightPath)
                ? DirStatus.Different
                : DirStatus.Same;
            list.Add(new DirEntry(rel, status, li.Length, ri.Length, leftPath, rightPath));
        }

        // ===== 第二趟：右侧剩下来的就是「仅右侧」（左侧没有的相对路径） =====
        // 用 ContainsKey 反查，不消耗枚举器；OnlyLeft 与 Same/Different 都已在第一趟记过，这里不会重复
        foreach (var (rel, rightPath) in rightFiles)
        {
            if (!leftFiles.ContainsKey(rel))
                list.Add(new DirEntry(rel, DirStatus.OnlyRight, null, new FileInfo(rightPath).Length, null, rightPath));
        }

        // 排序用 OrdinalIgnoreCase：贴合 Windows 资源管理器的观感，也避免大小写不同导致顺序抖动
        return list.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 递归索引一个目录：得到「相对根目录的路径 → 文件绝对路径」字典，键大小写不敏感
    /// （Windows 文件名不区分大小写，README.md 与 readme.md 视为同一个）。
    /// </summary>
    /// <param name="root">目录根路径</param>
    /// <returns>相对路径到全路径的映射；同一路径重复出现时后写覆盖前写（正常不会发生）</returns>
    private static Dictionary<string, string> Index(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // EnumerateFiles + AllDirectories：边枚举边建表，不像 GetFiles 先攒一个大数组；
        // 注意它遇到无权限的子目录会抛 UnauthorizedAccessException，由上层 catch 兜住
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, file)] = file;
        return map;
    }

    /// <summary>
    /// 逐字节比较两个文件内容。调用方已先比过大小（大小不等不会走到这里），
    /// 但打开后仍再核一次 Length 作为短路条件——文件可能在 stat 与打开之间被改掉。
    /// </summary>
    /// <param name="a">左侧文件路径</param>
    /// <param name="b">右侧文件路径</param>
    /// <returns>true = 内容完全一致；false = 有任一字节不同（含长度不一致）</returns>
    private static bool SameContent(string a, string b)
    {
        // using：两个流都是非托管句柄，边比边读，比较完立即释放，不会长期占着文件
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        // 长度不等直接 false：不必读内容（这也是「先比大小再比字节」的第二道保险）
        if (fa.Length != fb.Length) return false;

        // 80KB 双缓冲分块比：既不会把大文件整篇读进内存，又能把系统调用次数摊薄到可接受
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            // ReadAtLeast(throwOnEndOfStream: false)：读不满也返回实际字节数（即最后一块），
            // 不用 while 手工拼「短读续读」，代码更短且对慢盘同样正确
            int readA = fa.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            int readB = fb.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            // 两边读到的块长不同 → 内容长度对不上，直接判不等
            if (readA != readB) return false;
            // 双双读到 0 = 一起到结尾，前面每一块都比过且相等，判相同
            if (readA == 0) return true;
            // 只比实际读到的前缀（AsSpan 是零拷贝视图），SequenceEqual 走逐字节比较
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }

    /// <summary>
    /// 按「只显示差异」开关重筛结果表，并刷新计数统计。
    /// 只重建 ItemsSource，不重跑对比——这是它能在勾选时即时响应的原因。
    /// </summary>
    private void RefreshView()
    {
        bool onlyDiff = _onlyDiff.IsChecked == true;
        // 取消勾选时 !onlyDiff 恒真，等于全量显示；勾选时把 Same 滤掉
        // ToList 是必要的：LINQ 是惰性的，直接赋 IEnumerable 会让 ListView 反复重枚举同一份 _entries
        _list.ItemsSource = _entries
            .Where(e => !onlyDiff || e.Status != DirStatus.Same)
            .ToList();

        // ===== 统计永远基于全量 _entries，不受筛选影响，否则「只显示差异」时看不到相同项数量 =====
        int onlyLeft = _entries.Count(e => e.Status == DirStatus.OnlyLeft);
        int onlyRight = _entries.Count(e => e.Status == DirStatus.OnlyRight);
        int different = _entries.Count(e => e.Status == DirStatus.Different);
        int same = _entries.Count(e => e.Status == DirStatus.Same);
        UpdateStatus($"共 {_entries.Count} 个文件：仅左侧 {onlyLeft}，仅右侧 {onlyRight}，内容不同 {different}，相同 {same}");
    }

    /// <summary>
    /// 打开当前选中的结果行：两侧都有文件就送去逐行对比，只有一侧就用编辑器打开那一个。
    /// 触发点有两个——「打开选中」按钮与结果表双击。
    /// </summary>
    private void OpenSelected()
    {
        // 没有选中行（或选中了分组头等非 DirEntry 项）时什么都不做，不弹错误框
        if (_list.SelectedItem is not DirEntry entry) return;
        if (entry.LeftPath != null && entry.RightPath != null)
            CompareFilesRequested?.Invoke(entry.LeftPath, entry.RightPath);
        else
            // 单侧文件：LeftPath 为 null 时取 RightPath（两个路径至少有一个非空，否则这行不成其为结果），
            // 末尾的 ! 只是给编译器看，运行时不做任何事
            OpenFileRequested?.Invoke(entry.LeftPath ?? entry.RightPath!);
    }

    /// <summary>写底部状态栏文字（配色与 AutomationId 已在建工具栏时设好，这里只换文案）。</summary>
    /// <param name="text">提示文案</param>
    private void UpdateStatus(string text) => _status.Text = text;

    /// <summary>单个文件的对比结论：相同 / 内容不同 / 仅左侧 / 仅右侧。</summary>
    public enum DirStatus { Same, Different, OnlyLeft, OnlyRight } // 四个成员依次为：相同 / 内容不同 / 仅左侧有 / 仅右侧有

    /// <summary>一行对比结果；交换左右时把两边的路径和大小对调</summary>
    public sealed class DirEntry
    {
        /// <summary>
        /// 造一行结果。大小与路径都允许为 null，含义是「这一侧没有该文件」，界面上显示成「—」。
        /// </summary>
        /// <param name="relativePath">相对各自根目录的路径，用它在两侧对齐，也是排序键</param>
        /// <param name="status">对比结论</param>
        /// <param name="leftSize">左侧文件字节数；仅右侧时为 null</param>
        /// <param name="rightSize">右侧文件字节数；仅左侧时为 null</param>
        /// <param name="leftPath">左侧文件绝对路径；不存在为 null</param>
        /// <param name="rightPath">右侧文件绝对路径；不存在为 null</param>
        public DirEntry(string relativePath, DirStatus status, long? leftSize, long? rightSize,
                        string? leftPath, string? rightPath)
        {
            // 只读入仓：RelativePath 建好即不可变（它是排序键与表格列），
            // 其余四项可写，是为了支持「交换左右」原地对调
            RelativePath = relativePath;
            Status = status;
            LeftSize = leftSize;
            RightSize = rightSize;
            LeftPath = leftPath;
            RightPath = rightPath;
        }

        /// <summary>相对各自根目录的路径（两目录下的同一个相对位置），也是排序键与第一列显示内容</summary>
        public string RelativePath { get; }
        /// <summary>对比结论；交换左右时 OnlyLeft / OnlyRight 会互换</summary>
        public DirStatus Status { get; set; }
        /// <summary>左侧文件字节数；null = 左侧没有这个文件</summary>
        public long? LeftSize { get; set; }
        /// <summary>右侧文件字节数；null = 右侧没有这个文件</summary>
        public long? RightSize { get; set; }
        /// <summary>左侧文件绝对路径，供「打开/对比」时直接传给主窗口；null = 左侧没有</summary>
        public string? LeftPath { get; set; }
        /// <summary>右侧文件绝对路径；null = 右侧没有</summary>
        public string? RightPath { get; set; }

        /// <summary>状态列的中文文案，由 <see cref="Status"/> 翻译而来（GridView 直接绑这个只读属性）</summary>
        public string StatusText => Status switch
        {
            DirStatus.Same => "相同",
            DirStatus.Different => "内容不同",
            DirStatus.OnlyLeft => "仅左侧",
            _ => "仅右侧",
        };

        /// <summary>左大小列文案；<see cref="LeftSize"/> 为 null（仅右侧）时显示破折号占位</summary>
        public string LeftSizeText => LeftSize is long l ? SizeText(l) : "—";
        /// <summary>右大小列文案；<see cref="RightSize"/> 为 null（仅左侧）时显示破折号占位</summary>
        public string RightSizeText => RightSize is long r ? SizeText(r) : "—";

        /// <summary>
        /// 把字节数换算成人好读的单位：B / KB / MB 三档，KB 一位小数、MB 两位小数。
        /// 阈值用 1024 的幂（二进制口径），与资源管理器一致。
        /// </summary>
        /// <param name="bytes">原始字节数</param>
        /// <returns>带单位与适当精度的字符串</returns>
        private static string SizeText(long bytes) => bytes < 1024 ? bytes + " B"
            : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("N1") + " KB"
            : (bytes / 1024.0 / 1024.0).ToString("N2") + " MB";

        /// <summary>
        /// 原地把这一行的左右对调：路径、大小互换，「仅左侧/仅右侧」取反，
        /// 「相同/内容不同」与左右无关，保持原样。配合窗口的「交换左右」，省一次全盘重扫。
        /// </summary>
        internal void Swap()
        {
            (LeftPath, RightPath) = (RightPath, LeftPath);
            (LeftSize, RightSize) = (RightSize, LeftSize);
            Status = Status switch
            {
                DirStatus.OnlyLeft => DirStatus.OnlyRight,
                DirStatus.OnlyRight => DirStatus.OnlyLeft,
                _ => Status,
            };
        }
    }
}
