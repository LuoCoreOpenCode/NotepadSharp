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
    public event EventHandler<DirMatch>? JumpRequested;
    public event EventHandler? CloseRequested;
    /// <summary>目录替换写盘完成后，把改动过的文件路径交给宿主刷新对应标签页</summary>
    public event EventHandler<IReadOnlyList<string>>? ReplaceCompleted;

    private CancellationTokenSource? _cts;

    public DirFindPanel()
    {
        InitializeComponent();
        var saved = SettingsService.Current;
        if (saved.DirFindFilter.Length > 0) FilterTextBox.Text = saved.DirFindFilter;
        if (saved.DirFindSkipDirNames.Length > 0) SkipDirNamesTextBox.Text = saved.DirFindSkipDirNames;
        MaxFileMbTextBox.Text = saved.DirFindMaxFileMb.ToString();
        SkipBinaryCheckBox.IsChecked = saved.DirFindSkipBinary;
        SkipBigFileCheckBox.IsChecked = saved.DirFindSkipBigFile;
        RefreshHistory();
    }

    /// <summary>用当前文件所在目录或给定目录预填</summary>
    public void Prepare(string? rootPath, string? keyword)
    {
        string root = !string.IsNullOrEmpty(rootPath) ? rootPath : SettingsService.Current.DirFindRoot;
        if (root.Length > 0) RootTextBox.Text = root;
        if (!string.IsNullOrEmpty(keyword)) FindCombo.Text = keyword;
        FindCombo.Focus();
        if (FindCombo.Template?.FindName("PART_EditableTextBox", FindCombo) is TextBox box) box.SelectAll();
    }

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

        return new DirSearchOptions
        {
            RootPath = root,
            FindText = FindCombo.Text,
            ReplaceText = ReplaceCombo.Text,
            Mode = ModeRegexRadio.IsChecked == true ? SearchMode.Regex
                 : ModeExtendRadio.IsChecked == true ? SearchMode.Extend
                 : SearchMode.Normal,
            MatchCase = MatchCaseCheckBox.IsChecked == true,
            WholeWord = WholeWordCheckBox.IsChecked == true,
            SkipHidden = SkipHiddenCheckBox.IsChecked == true,
            SkipChildDirs = SkipChildDirsCheckBox.IsChecked == true,
            SkipBinary = SkipBinaryCheckBox.IsChecked == true,
            SkipBigFile = SkipBigFileCheckBox.IsChecked == true,
            MaxFileMb = ParseMb(MaxFileMbTextBox.Text),
            FilterByFileType = FileTypeCheckBox.IsChecked == true,
            FileFilter = FilterTextBox.Text,
            SkipDirNames = SkipDirNamesCheckBox.IsChecked == true,
            SkipDirNameList = SkipDirNamesTextBox.Text,
        };
    }

    private static int ParseMb(string text)
        => int.TryParse(text.Trim(), out int mb) && mb > 0 ? mb : 20;

    /// <summary>校验通过后记住这次的条件，下次打开面板直接可用</summary>
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

    private void RefreshHistory()
    {
        var settings = SettingsService.Current;
        FindCombo.ItemsSource = settings.FindHistory.ToArray();
        ReplaceCombo.ItemsSource = settings.ReplaceHistory.ToArray();
    }

    /// <summary>ND2 在目录查找/替换后把关键字写进同一份查找历史</summary>
    private void AddHistory(DirSearchOptions options)
    {
        var settings = SettingsService.Current;
        bool changed = false;
        if (options.FindText.Length > 0) { Prepend(settings.FindHistory, options.FindText); changed = true; }
        if (options.ReplaceText.Length > 0) { Prepend(settings.ReplaceHistory, options.ReplaceText); changed = true; }
        if (changed)
        {
            SettingsService.Save();
            RefreshHistory();
        }
    }

    private static void Prepend(List<string> history, string value)
    {
        history.RemoveAll(h => string.Equals(h, value, StringComparison.Ordinal));
        history.Insert(0, value);
        while (history.Count > 25) history.RemoveAt(history.Count - 1);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要查找的目录",
            SelectedPath = Directory.Exists(RootTextBox.Text) ? RootTextBox.Text : Environment.CurrentDirectory,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            RootTextBox.Text = dlg.SelectedPath;
    }

    private void OnSearch(object sender, RoutedEventArgs e) => StartSearch();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) StartSearch();
    }

    private void StartSearch()
    {
        var options = BuildOptions(out var error);
        if (error != null)
        {
            ProgressText.Text = error;
            return;
        }

        ResultTree.Items.Clear();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Remember(options);
        AddHistory(options);
        SetRunning(true);

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

        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await DirectorySearchService.SearchAsync(options, progress, token, ask);
                owner?.Dispatcher.BeginInvoke(() => ShowResults(summary));
            }
            catch (OperationCanceledException)
            {
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "已取消");
            }
            catch (Exception ex)
            {
                owner?.Dispatcher.BeginInvoke(() => ProgressText.Text = "查找失败：" + ex.Message);
            }
            finally
            {
                owner?.Dispatcher.BeginInvoke(() => SetRunning(false));
            }
        }, token);
    }

    private void ShowResults(DirectorySearchService.DirSearchSummary summary)
    {
        ResultTree.Items.Clear();
        foreach (var file in summary.Results)
        {
            var fileNode = new TreeViewItem
            {
                Header = $"{file.DisplayName}（{file.Matches.Count} 处）— {file.DirectoryName}",
                Tag = file,
            };
            foreach (var match in file.Matches)
            {
                fileNode.Items.Add(new TreeViewItem
                {
                    Header = $"行 {match.Line}: {match.LineText.Trim()}",
                    Tag = match,
                });
            }
            ResultTree.Items.Add(fileNode);
            fileNode.IsExpanded = summary.Results.Count <= 5;
        }
        ProgressText.Text = summary.Matches == 0
            ? $"查找完成，遍历 {summary.FilesWalked} 个文件，没有找到「{FindCombo.Text}」"
            : $"查找完成，遍历 {summary.FilesWalked} 个文件，一共 {summary.Matches} 处发现在 {summary.HitFiles} 个文件！（双击跳转）";
    }

    private void OnClearResult(object sender, RoutedEventArgs e)
    {
        ResultTree.Items.Clear();
        ProgressText.Text = "已清空结果。";
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
        => JumpToSelected();

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) JumpToSelected();
    }

    private void JumpToSelected()
    {
        if (ResultTree.SelectedItem is TreeViewItem { Tag: DirMatch match })
            JumpRequested?.Invoke(this, match);
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions(out var error);
        if (error != null)
        {
            ProgressText.Text = error;
            return;
        }

        var owner = Window.GetWindow(this);
        var confirm = MessageBox.Show(owner,
            $"确定把「{options.FindText}」全部替换为「{options.ReplaceText}」吗？\n" +
            $"目标目录：{options.RootPath}\n改动会直接写入磁盘（覆盖已打开的同名标签页）。",
            "在目录文件中替换", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Remember(options);
        AddHistory(options);
        SetRunning(true);

        var progress = new Progress<DirProgress>(p =>
            ProgressText.Text = p.Done
                ? $"正在汇总：已替换 {p.FilesScanned} 个文件，{p.MatchesFound} 处"
                : $"正在替换 {p.FilesScanned} 个文件，{p.MatchesFound} 处…{p.CurrentFile}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (files, count, changed, skipped) = await DirectorySearchService.ReplaceAsync(options, progress, token);
                owner?.Dispatcher.BeginInvoke(() =>
                {
                    ProgressText.Text = $"替换完成，遍历 {files} 个文件，一共 {count} 处替换！"
                        + (skipped.Count > 0 ? $" 跳过 {skipped.Count} 个：" + string.Join("；", skipped.Take(5)) : string.Empty);
                    ResultTree.Items.Clear();
                    ReplaceCompleted?.Invoke(this, changed);
                });
            }
            catch (OperationCanceledException)
            {
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

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetRunning(bool running)
    {
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        FindCombo.IsEnabled = !running;
        RootTextBox.IsEnabled = !running;
        FindAllButton.IsEnabled = !running;
        ReplaceAllButton.IsEnabled = !running;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
