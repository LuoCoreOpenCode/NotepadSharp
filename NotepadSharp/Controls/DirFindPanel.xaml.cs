using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotepadSharp.Services;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NotepadSharp.Controls;

/// <summary>
/// 目录查找/替换面板：在宿主窗体底部展开，
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
    }

    /// <summary>用当前文件所在目录或给定目录预填</summary>
    public void Prepare(string? rootPath, string? keyword)
    {
        string root = !string.IsNullOrEmpty(rootPath) ? rootPath : SettingsService.Current.DirFindRoot;
        if (root.Length > 0) RootTextBox.Text = root;
        if (!string.IsNullOrEmpty(keyword)) FindTextBox.Text = keyword;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private DirSearchOptions BuildOptions(out string? error)
    {
        error = null;
        var root = RootTextBox.Text.Trim();
        if (root.Length == 0 || !Directory.Exists(root))
        {
            error = "目录不存在";
            return new DirSearchOptions();
        }
        if (FindTextBox.Text.Length == 0)
        {
            error = "请输入查找内容";
            return new DirSearchOptions();
        }

        return new DirSearchOptions
        {
            RootPath = root,
            FindText = FindTextBox.Text,
            FileFilter = FilterTextBox.Text,
            Recursive = RecursiveCheckBox.IsChecked == true,
            SkipHidden = SkipHiddenCheckBox.IsChecked == true,
            MatchCase = MatchCaseCheckBox.IsChecked == true,
            WholeWord = WholeWordCheckBox.IsChecked == true,
            Regex = RegexCheckBox.IsChecked == true,
        };
    }

    /// <summary>校验通过后记住目录与文件类型，下次打开面板直接可用</summary>
    private void Remember(DirSearchOptions options)
    {
        var settings = SettingsService.Current;
        settings.DirFindRoot = options.RootPath;
        settings.DirFindFilter = options.FileFilter;
        SettingsService.Save();
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
        SetRunning(true);

        var progress = new Progress<DirProgress>(p =>
        {
            ProgressText.Text = p.Done
                ? $"扫描 {p.FilesScanned} 个文件，命中 {p.MatchesFound} 处"
                : $"正在扫描 {p.FilesScanned} 个文件，命中 {p.MatchesFound} 处…{p.CurrentFile}";
        });

        var owner = Window.GetWindow(this);
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await DirectorySearchService.SearchAsync(options, progress, token);
                owner?.Dispatcher.BeginInvoke(() => ShowResults(results));
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

    private void ShowResults(List<DirFileResult> results)
    {
        ResultTree.Items.Clear();
        int total = 0;
        foreach (var file in results)
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
                total++;
            }
            ResultTree.Items.Add(fileNode);
            fileNode.IsExpanded = results.Count <= 5;
        }
        ProgressText.Text = total == 0
            ? $"扫描完成，未找到「{FindTextBox.Text}」"
            : $"{results.Count} 个文件，{total} 处命中（双击跳转）";
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
            $"将在 {options.RootPath} 下的 {options.FileFilter} 文件中把「{options.FindText}」替换为「{ReplaceTextBox.Text}」。\n" +
            "改动会直接写入磁盘（覆盖已打开的同名标签页）。是否继续？",
            "目录替换", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        string replaceText = ReplaceTextBox.Text;   // 后台线程不能直接读控件属性
        Remember(options);
        SetRunning(true);

        var progress = new Progress<DirProgress>(p =>
            ProgressText.Text = p.Done
                ? $"替换完成：{p.FilesScanned} 个文件，{p.MatchesFound} 处"
                : $"正在替换 {p.FilesScanned} 个文件，{p.MatchesFound} 处…{p.CurrentFile}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (files, count, changed, skipped) = await DirectorySearchService.ReplaceAsync(
                    options, replaceText, progress, token);
                owner?.Dispatcher.BeginInvoke(() =>
                {
                    ProgressText.Text = $"{files} 个文件、{count} 处已替换"
                        + (skipped.Count > 0 ? $"；跳过 {skipped.Count} 个：" + string.Join("；", skipped.Take(5)) : string.Empty);
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
        FindTextBox.IsEnabled = !running;
        RootTextBox.IsEnabled = !running;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
