using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
/// 对齐 ndd RealCompare 的目录模式；双击两边都有的文件直接进左右对比。
/// </summary>
public sealed class DirCompareWindow : Window
{
    private readonly TextBox _leftDir = new() { Text = string.Empty };
    private readonly TextBox _rightDir = new() { Text = string.Empty };
    private readonly ListView _list = new();
    private readonly TextBlock _status = new();
    private readonly CheckBox _onlyDiff = new() { Content = "只显示差异", IsChecked = true };
    private readonly List<DirEntry> _entries = new();
    private CancellationTokenSource? _cts;

    public Action<string>? OpenFileRequested { get; set; }
    public Action<string, string>? CompareFilesRequested { get; set; }

    public DirCompareWindow()
    {
        Title = "目录对比";
        Width = 1000;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        var root = new DockPanel { Margin = new Thickness(8) };

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);

        BuildList();
        root.Children.Add(_list);
        Content = root;

        _onlyDiff.Checked += (_, _) => RefreshView();
        _onlyDiff.Unchecked += (_, _) => RefreshView();
        Closed += (_, _) => _cts?.Cancel();
        UpdateStatus("填两个目录（可以直接粘贴路径），然后点「开始对比」；双击结果行可打开差异。");
    }

    public void SetDirectories(string left, string? right = null)
    {
        _leftDir.Text = left;
        if (!string.IsNullOrEmpty(right)) _rightDir.Text = right;
    }

    private UIElement BuildToolbar()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

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
        _status.Foreground = Application.Current.Resources["TextSecondaryBrush"] as Brush;
        _status.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetAutomationId(_status, "DirStatus");
        return panel;
    }

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
        _list.MouseDoubleClick += (_, _) => OpenSelected();
    }

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

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private static FrameworkElement Gap(double width) => new Border { Width = width };

    private Button TextButton(string label, Action action)
    {
        var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private void BrowseInto(TextBox target)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
            dlg.SelectedPath = target.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private void Swap()
    {
        (_leftDir.Text, _rightDir.Text) = (_rightDir.Text, _leftDir.Text);
        foreach (var e in _entries) e.Swap();
        RefreshView();
    }

    private async Task RunCompareAsync()
    {
        string a = _leftDir.Text.Trim(), b = _rightDir.Text.Trim();
        if (!Directory.Exists(a) || !Directory.Exists(b))
        {
            MessageBox.Show(this, "左右两个目录都要存在。", "目录对比", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "左右是同一个目录。", "目录对比", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        UpdateStatus("正在扫描…");

        List<DirEntry> result;
        try
        {
            result = await Task.Run(() => CompareDirectories(a, b, token), token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("已停止。");
            return;
        }
        catch (Exception ex)
        {
            UpdateStatus("对比失败：" + ex.Message);
            return;
        }

        _entries.Clear();
        _entries.AddRange(result);
        RefreshView();
    }

    private static List<DirEntry> CompareDirectories(string left, string right, CancellationToken token)
    {
        var leftFiles = Index(left);
        var rightFiles = Index(right);
        var list = new List<DirEntry>();

        foreach (var (rel, leftPath) in leftFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!rightFiles.TryGetValue(rel, out var rightPath))
            {
                list.Add(new DirEntry(rel, DirStatus.OnlyLeft, new FileInfo(leftPath).Length, null, leftPath, null));
                continue;
            }
            var li = new FileInfo(leftPath);
            var ri = new FileInfo(rightPath);
            var status = li.Length != ri.Length || !SameContent(leftPath, rightPath)
                ? DirStatus.Different
                : DirStatus.Same;
            list.Add(new DirEntry(rel, status, li.Length, ri.Length, leftPath, rightPath));
        }

        foreach (var (rel, rightPath) in rightFiles)
        {
            if (!leftFiles.ContainsKey(rel))
                list.Add(new DirEntry(rel, DirStatus.OnlyRight, null, new FileInfo(rightPath).Length, null, rightPath));
        }

        return list.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> Index(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, file)] = file;
        return map;
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;

        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            int readA = fa.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            int readB = fb.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }

    private void RefreshView()
    {
        bool onlyDiff = _onlyDiff.IsChecked == true;
        _list.ItemsSource = _entries
            .Where(e => !onlyDiff || e.Status != DirStatus.Same)
            .ToList();

        int onlyLeft = _entries.Count(e => e.Status == DirStatus.OnlyLeft);
        int onlyRight = _entries.Count(e => e.Status == DirStatus.OnlyRight);
        int different = _entries.Count(e => e.Status == DirStatus.Different);
        int same = _entries.Count(e => e.Status == DirStatus.Same);
        UpdateStatus($"共 {_entries.Count} 个文件：仅左侧 {onlyLeft}，仅右侧 {onlyRight}，内容不同 {different}，相同 {same}");
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is not DirEntry entry) return;
        if (entry.LeftPath != null && entry.RightPath != null)
            CompareFilesRequested?.Invoke(entry.LeftPath, entry.RightPath);
        else
            OpenFileRequested?.Invoke(entry.LeftPath ?? entry.RightPath!);
    }

    private void UpdateStatus(string text) => _status.Text = text;

    public enum DirStatus { Same, Different, OnlyLeft, OnlyRight }

    /// <summary>一行对比结果；交换左右时把两边的路径和大小对调</summary>
    public sealed class DirEntry
    {
        public DirEntry(string relativePath, DirStatus status, long? leftSize, long? rightSize,
                        string? leftPath, string? rightPath)
        {
            RelativePath = relativePath;
            Status = status;
            LeftSize = leftSize;
            RightSize = rightSize;
            LeftPath = leftPath;
            RightPath = rightPath;
        }

        public string RelativePath { get; }
        public DirStatus Status { get; set; }
        public long? LeftSize { get; set; }
        public long? RightSize { get; set; }
        public string? LeftPath { get; set; }
        public string? RightPath { get; set; }

        public string StatusText => Status switch
        {
            DirStatus.Same => "相同",
            DirStatus.Different => "内容不同",
            DirStatus.OnlyLeft => "仅左侧",
            _ => "仅右侧",
        };

        public string LeftSizeText => LeftSize is long l ? SizeText(l) : "—";
        public string RightSizeText => RightSize is long r ? SizeText(r) : "—";

        private static string SizeText(long bytes) => bytes < 1024 ? bytes + " B"
            : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("N1") + " KB"
            : (bytes / 1024.0 / 1024.0).ToString("N2") + " MB";

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
