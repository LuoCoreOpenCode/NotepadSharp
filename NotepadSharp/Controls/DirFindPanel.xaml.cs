using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotepadSharp.Services;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NotepadSharp.Controls;

/// <summary>
/// 目录查找/替换面板：在宿主窗体底部展开，对应 ND2 查找窗「在目录查找」那一页。
/// 结果按文件分组，双击命中行跳转；替换直接按原编码写回磁盘。
/// </summary>
public partial class DirFindPanel : UserControl
{
    // 面板自己不开文件、也不写磁盘：所有跨标签页的动作都靠这三个事件交给宿主 MainWindow 完成。

    /// <summary>双击结果树叶子节点（或在该节点上按回车）：把这条命中交给宿主打开文件并定位</summary>
    public event EventHandler<DirMatch>? JumpRequested;
    /// <summary>「✕ 关闭」按钮：宿主收起面板并把焦点还给编辑器</summary>
    public event EventHandler? CloseRequested;
    /// <summary>目录替换写盘完成后，把改动过的文件路径交给宿主刷新对应标签页</summary>
    public event EventHandler<IReadOnlyList<string>>? ReplaceCompleted;

    /// <summary>
    /// 本轮扫描/替换的取消令牌源：「停止」按钮、「✕ 关闭」以及新一轮开始时都会 Cancel 它。
    /// 只保留最新一个实例，替换掉旧的之前一定先取消，避免两个后台任务同时往同一个树里写结果。
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 构造面板：装配 XAML 生成的控件，再把上次退出时记住的目录查找条件回填到界面上。
    /// </summary>
    public DirFindPanel()
    {
        InitializeComponent();
        var saved = SettingsService.Current;
        // 只有非空才覆盖：XAML 里给的是出厂默认值（*.* / 20 / debug:Debug:...），
        // 设置里存的是空串时应当保留默认，不能把输入框清空。
        if (saved.DirFindFilter.Length > 0) FilterTextBox.Text = saved.DirFindFilter;
        if (saved.DirFindSkipDirNames.Length > 0) SkipDirNamesTextBox.Text = saved.DirFindSkipDirNames;
        MaxFileMbTextBox.Text = saved.DirFindMaxFileMb.ToString();
        SkipBinaryCheckBox.IsChecked = saved.DirFindSkipBinary;
        SkipBigFileCheckBox.IsChecked = saved.DirFindSkipBigFile;
        // 查找/替换词下拉复用文档查找那两份历史，与 ND2 同一个下拉数据源
        RefreshHistory();
    }

    /// <summary>用当前文件所在目录或给定目录预填</summary>
    /// <param name="rootPath">优先使用的目录（宿主从「当前文件所在目录」取来）；为空则退回设置里上次的根目录</param>
    /// <param name="keyword">要预填进查找框的词（通常是编辑器里的选中文本）；为空则不动查找框</param>
    public void Prepare(string? rootPath, string? keyword)
    {
        // 目录来源两级：调用方给的 → 设置里记住的上次目录
        string root = !string.IsNullOrEmpty(rootPath) ? rootPath : SettingsService.Current.DirFindRoot;
        if (root.Length > 0) RootTextBox.Text = root;
        if (!string.IsNullOrEmpty(keyword)) FindCombo.Text = keyword;
        FindCombo.Focus();
        // 可编辑 ComboBox 本身不是 TextBox，全选要走模板里的 PART_EditableTextBox
        if (FindCombo.Template?.FindName("PART_EditableTextBox", FindCombo) is TextBox box) box.SelectAll();
    }

    /// <summary>
    /// 把界面上十来个控件的状态收拢成一次目录查找/替换的入参，顺便做必填校验。
    /// 校验顺序照 ND2：先目录、再目录存在性、最后关键字，任一不过就把话术写进 error。
    /// </summary>
    /// <param name="error">校验失败时是给用户看的中文提示；通过时为 null</param>
    /// <returns>校验通过时是本次的完整条件；失败时是字段全为默认值的空壳，调用方应直接丢弃并显示 error</returns>
    private DirSearchOptions BuildOptions(out string? error)
    {
        error = null;
        var root = RootTextBox.Text.Trim();
        if (root.Length == 0)
        {
            error = "请选择目标文件夹！";
            return new DirSearchOptions();
        }
        if (!Directory.Exists(root))
        {
            error = $"目标目录 {root} 不存在！";
            return new DirSearchOptions();
        }
        if (FindCombo.Text.Length == 0)
        {
            error = "查找字段为空";
            return new DirSearchOptions();
        }

        // 三态单选：正则优先、扩展次之、其余为普通，与 ND2 的 radio 互斥语义一致
        return new DirSearchOptions
        {
            RootPath = root,
            FindText = FindCombo.Text,
            ReplaceText = ReplaceCombo.Text,
            Mode = ModeRegexRadio.IsChecked == true ? SearchMode.Regex
                 : ModeExtendRadio.IsChecked == true ? SearchMode.Extend
                 : SearchMode.Normal,
            // 大小写/全词只反映勾选；普通与扩展走 Ordinal 系比较，正则由 ToRegexOptions 换算
            MatchCase = MatchCaseCheckBox.IsChecked == true,
            WholeWord = WholeWordCheckBox.IsChecked == true,
            // 下面四条是「少扫一点」的护栏开关，对应 ND2 skipChildDirs / skipHidden / skipBinary / skipFileMaxSize
            SkipHidden = SkipHiddenCheckBox.IsChecked == true,
            SkipChildDirs = SkipChildDirsCheckBox.IsChecked == true,
            SkipBinary = SkipBinaryCheckBox.IsChecked == true,
            SkipBigFile = SkipBigFileCheckBox.IsChecked == true,
            MaxFileMb = ParseMb(MaxFileMbTextBox.Text),
            // 不勾「文件类型」时服务侧按所有文件处理；模式串是 *.* : *.cpp 这种冒号分隔写法
            FilterByFileType = FileTypeCheckBox.IsChecked == true,
            FileFilter = FilterTextBox.Text,
            SkipDirNames = SkipDirNamesCheckBox.IsChecked == true,
            SkipDirNameList = SkipDirNamesTextBox.Text,
        };
    }

    /// <summary>
    /// 解析「跳过超过 N MB 的文件」里的手填数字：非数字、空或 ≤0 都当没填，落回默认 20。
    /// </summary>
    /// <param name="text">大小输入框的原文</param>
    /// <returns>合法的 MB 上限；解析失败时为 20</returns>
    private static int ParseMb(string text)
        => int.TryParse(text.Trim(), out int mb) && mb > 0 ? mb : 20;

    /// <summary>校验通过后记住这次的条件，下次打开面板直接可用</summary>
    /// <param name="options">刚通过 BuildOptions 校验的本次条件；只取其中值得复用的几项落盘</param>
    private void Remember(DirSearchOptions options)
    {
        var settings = SettingsService.Current;
        settings.DirFindRoot = options.RootPath;
        settings.DirFindFilter = options.FileFilter;
        settings.DirFindSkipBinary = options.SkipBinary;
        settings.DirFindSkipBigFile = options.SkipBigFile;
        settings.DirFindMaxFileMb = options.MaxFileMb;
        settings.DirFindSkipDirNames = options.SkipDirNameList;
        SettingsService.Save();
    }

    /// <summary>
    /// 把历史词挂到两个下拉框上：与文档查找面板共用同一份 FindHistory/ReplaceHistory，
    /// 复制成数组是给 ItemsSource 一份静态快照，不会被后续追加搞出重复项。
    /// </summary>
    private void RefreshHistory()
    {
        var settings = SettingsService.Current;
        FindCombo.ItemsSource = settings.FindHistory.ToArray();
        ReplaceCombo.ItemsSource = settings.ReplaceHistory.ToArray();
    }

    /// <summary>ND2 在目录查找/替换后把关键字写进同一份查找历史</summary>
    /// <param name="options">本次条件；查找词与替换词分别入库，空词不记</param>
    private void AddHistory(DirSearchOptions options)
    {
        var settings = SettingsService.Current;
        bool changed = false;
        if (options.FindText.Length > 0) { Prepend(settings.FindHistory, options.FindText); changed = true; }
        if (options.ReplaceText.Length > 0) { Prepend(settings.ReplaceHistory, options.ReplaceText); changed = true; }
        // 只有真记了新词才落盘并重挂下拉，避免每次都白白刷新一次
        if (changed)
        {
            SettingsService.Save();
            RefreshHistory();
        }
    }

    /// <summary>
    /// 历史入库尾部：按原文（区分大小写）去掉同一条旧记录，插到最前，只留最近 25 条。
    /// 与 FindReplacePanel.Prepend 同一套规则，两个面板看到的是同一份历史。
    /// </summary>
    /// <param name="history">要原地修改的历史列表（设置对象里的实例）</param>
    /// <param name="value">本次要记住的文本</param>
    private static void Prepend(List<string> history, string value)
    {
        history.RemoveAll(h => string.Equals(h, value, StringComparison.Ordinal));
        history.Insert(0, value);
        while (history.Count > 25) history.RemoveAt(history.Count - 1);
    }

    /// <summary>「浏览…」按钮：弹系统目录选择框，选中的路径回填到目标目录输入框</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // 用 WinForms 的 FolderBrowserDialog：WPF 自身没有目录选择器，项目已开 UseWindowsForms
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要查找的目录",
            // 当前文本是合法目录就从那儿起步，否则退回进程工作目录，避免每次从 C:\ 根翻起
            SelectedPath = Directory.Exists(RootTextBox.Text) ? RootTextBox.Text : Environment.CurrentDirectory,
        };
        // 用户取消时不动输入框，保留原来填的内容
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            RootTextBox.Text = dlg.SelectedPath;
    }

    /// <summary>「查找所有」按钮：走一遍与回车相同的启动流程</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnSearch(object sender, RoutedEventArgs e) => StartSearch();

    /// <summary>查找框里按回车：等价于点「查找所有」，不用把手挪到按钮上</summary>
    /// <param name="sender">查找下拉框，未使用</param>
    /// <param name="e">按键事件，只看是否为回车；不置 Handled，下拉的展开行为保持原样</param>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) StartSearch();
    }

    /// <summary>
    /// 启动一轮目录查找：校验条件 → 清空旧结果 → 取消上一轮 → 把扫描丢到后台线程，
    /// 期间用 Progress&lt;T&gt; 和 Dispatcher 把进度与结果送回 UI 线程。
    /// </summary>
    private void StartSearch()
    {
        var options = BuildOptions(out var error);
        if (error != null)
        {
            // 条件不合法就不启动任务，直接把中文提示当进度显示，和 ND2 的状态行一样
            ProgressText.Text = error;
            return;
        }

        ResultTree.Items.Clear();
        // 新一轮开始前先掐掉上一轮：旧任务仍在跑的话会往同一个树里追加结果
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Remember(options);
        AddHistory(options);
        SetRunning(true);

        // Progress<T> 是在 UI 线程上创建的，回调自动排回 UI 线程执行，所以这里可以直接改 Text
        var progress = new Progress<DirProgress>(p =>
        {
            ProgressText.Text = p.Done
                ? $"正在汇总：已扫描 {p.FilesScanned} 个文件，命中 {p.MatchesFound} 处"
                : $"正在扫描 {p.FilesScanned} 个文件，命中 {p.MatchesFound} 处…{p.CurrentFile}";
        });

        var owner = Window.GetWindow(this);
        // 询问要回到 UI 线程弹框，后台线程等结果（文案与 ND2 的 Continue Find ? 一致）
        Func<int, int, bool> ask = (hitFiles, matches) => owner == null || owner.Dispatcher.Invoke(() =>
            MessageBox.Show(owner,
                $"查找结果已经有 {matches} 处在 {hitFiles} 个文件中，结果太多会比较慢，是否继续查找？",
                "是否继续查找？", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        // 不 await：查找期间面板要能继续响应「停止」按钮；lambda 里全程只读 options，
        // 结果与异常都用 Dispatcher.BeginInvoke 投回 UI 线程刷新，后台线程不直接碰控件
        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await DirectorySearchService.SearchAsync(options, progress, token, ask);
                owner?.Dispatcher.BeginInvoke(() => ShowResults(summary));
            }
            catch (OperationCanceledException)
            {
                // 用户点「停止」：枚举器或行匹配处抛出来，只改状态行，不清掉已有结果
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "已取消");
            }
            catch (Exception ex)
            {
                // 目录权限、磁盘拔出等意外：把原因直接报在状态行，不弹窗打断
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "查找失败：" + ex.Message);
            }
            finally
            {
                // 无论成功、取消还是异常都要恢复按钮可用性，否则面板会永久卡在「进行中」
                owner?.Dispatcher.BeginInvoke(() => SetRunning(false));
            }
        }, token);
    }

    /// <summary>
    /// 在 UI 线程上把扫描汇总渲染成两层结果树：文件节点 + 命中行叶子，并写完成文案。
    /// </summary>
    /// <param name="summary">后台扫描的返回值：分组结果、遍历文件数、命中处数、命中文件数</param>
    private void ShowResults(DirectorySearchService.DirSearchSummary summary)
    {
        ResultTree.Items.Clear();
        foreach (var file in summary.Results)
        {
            // 组节点：文件名（命中数）— 所在目录；Tag 挂整个文件结果，叶子挂单条命中
            var fileNode = new TreeViewItem
            {
                Header = $"{file.DisplayName}（{file.Matches.Count} 处）— {file.DirectoryName}",
                Tag = file,
            };
            foreach (var match in file.Matches)
            {
                // 叶子节点：行号 1 基，与 ND2 结果窗一致；行内容 Trim 掉缩进只留可读部分
                fileNode.Items.Add(new TreeViewItem
                {
                    Header = $"行 {match.Line}: {match.LineText.Trim()}",
                    Tag = match,
                });
            }
            ResultTree.Items.Add(fileNode);
            // 文件不多时默认展开方便直接双击；多了就全部折起，避免一屏几百行
            fileNode.IsExpanded = summary.Results.Count <= 5;
        }
        ProgressText.Text = summary.Matches == 0
            ? $"查找完成，遍历 {summary.FilesWalked} 个文件，没有找到「{FindCombo.Text}」"
            : $"查找完成，遍历 {summary.FilesWalked} 个文件，一共 {summary.Matches} 处发现在 {summary.HitFiles} 个文件！（双击跳转）";
    }

    /// <summary>「清空结果」按钮：只清树和状态行，不动查找条件</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClearResult(object sender, RoutedEventArgs e)
    {
        ResultTree.Items.Clear();
        ProgressText.Text = "已清空结果。";
    }

    /// <summary>结果树鼠标双击：把选中的叶子交给宿主跳转（组节点没有 DirMatch 会被忽略）</summary>
    /// <param name="sender">结果树，未使用</param>
    /// <param name="e">鼠标事件，未使用（双击已让节点先成为选中项）</param>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
        => JumpToSelected();

    /// <summary>结果树里按回车：等价于双击，方便键盘操作</summary>
    /// <param name="sender">结果树，未使用</param>
    /// <param name="e">按键事件，只看回车；不置 Handled，方向键等仍由树自己处理</param>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) JumpToSelected();
    }

    /// <summary>
    /// 取树上选中项的 Tag：只有叶子节点挂的是 DirMatch，命中就发跳转事件；
    /// 选中的是文件组节点（Tag 是 DirFileResult）时模式不匹配，什么都不做。
    /// </summary>
    private void JumpToSelected()
    {
        if (ResultTree.SelectedItem is TreeViewItem { Tag: DirMatch match })
            JumpRequested?.Invoke(this, match);
    }

    /// <summary>
    /// 「在目录文件中替换」按钮：条件校验 → 危险操作确认框 → 后台批量写盘。
    /// 与查找不同，这一步会真的改磁盘文件（按原编码写回），所以必须先让用户确认。
    /// </summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions(out var error);
        if (error != null)
        {
            ProgressText.Text = error;
            return;
        }

        // 确认框文案把「替换成什么」和「目标目录」都摊开写清楚，避免误点在几百个文件上批量改写
        var owner = Window.GetWindow(this);
        var confirm = MessageBox.Show(owner,
            $"确定把「{options.FindText}」全部替换为「{options.ReplaceText}」吗？\n" +
            $"目标目录：{options.RootPath}\n改动会直接写入磁盘（覆盖已打开的同名标签页）。",
            "在目录文件中替换", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // 与 StartSearch 同一套生命周期：取消上一轮 → 建新令牌 → 记条件与历史 → 锁住输入控件
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Remember(options);
        AddHistory(options);
        SetRunning(true);

        // 替换场景下 DirProgress 的四个字段语义变化：FilesScanned 计发生改动的文件数，
        // MatchesFound 计替换次数，所以文案与查找那一路不同
        var progress = new Progress<DirProgress>(p =>
            ProgressText.Text = p.Done
                ? $"正在汇总：已替换 {p.FilesScanned} 个文件，{p.MatchesFound} 处"
                : $"正在替换 {p.FilesScanned} 个文件，{p.MatchesFound} 处…{p.CurrentFile}");

        // 同样不 await：结果回到 UI 线程时一次性刷新状态行、清空旧结果树，
        // 再把改动过的路径清单交给宿主去重载对应标签页
        _ = Task.Run(async () =>
        {
            try
            {
                var (files, count, changed, skipped) = await DirectorySearchService.ReplaceAsync(options, progress, token);
                owner?.Dispatcher.BeginInvoke(() =>
                {
                    // 跳过原因可能有几十条，状态行放不下，只挑前 5 条列出、总数照实报
                    ProgressText.Text = $"替换完成，遍历 {files} 个文件，一共 {count} 处替换！"
                        + (skipped.Count > 0 ? $" 跳过 {skipped.Count} 个：" + string.Join("；", skipped.Take(5)) : string.Empty);
                    ResultTree.Items.Clear();
                    ReplaceCompleted?.Invoke(this, changed);
                });
            }
            catch (OperationCanceledException)
            {
                // 注意：取消只保证不再继续，已经写回磁盘的文件不会回滚（服务侧的既定语义）
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "已取消");
            }
            catch (Exception ex)
            {
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "替换失败：" + ex.Message);
            }
            finally
            {
                owner?.Dispatcher.BeginInvoke(() => SetRunning(false));
            }
        }, token);
    }

    /// <summary>「停止」按钮：只发取消信号，已扫到的结果与已写盘的文件都保留原样</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// 切换「进行中/空闲」界面状态：跑着的时候只留「停止」可点，
    /// 并禁掉目录与查找输入框和两个执行按钮，防止用户在半路上改条件再开一轮。
    /// </summary>
    /// <param name="running">true 表示后台任务正在跑</param>
    private void SetRunning(bool running)
    {
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        FindCombo.IsEnabled = !running;
        RootTextBox.IsEnabled = !running;
        FindAllButton.IsEnabled = !running;
        ReplaceAllButton.IsEnabled = !running;
    }

    /// <summary>「✕ 关闭」按钮：先掐掉可能还在跑的后台任务，再通知宿主收起面板</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
