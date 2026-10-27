using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotepadSharp.Controls;
using NotepadSharp.Services;
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
/// </summary>
public sealed class CompareWindow : Window
{
    private readonly EditorControl _left = new();
    private readonly EditorControl _right = new();
    private readonly TextBlock _leftLabel = new();
    private readonly TextBlock _rightLabel = new();
    private readonly TextBlock _status = new();
    private readonly CheckBox _ignoreCase = new() { Content = "忽略大小写" };
    private readonly CheckBox _ignoreTrivia = new() { Content = "忽略行尾空白" };

    private string _pathA = string.Empty;
    private string _pathB = string.Empty;
    private string[] _linesA = Array.Empty<string>();
    private string[] _linesB = Array.Empty<string>();
    private List<DiffLine> _diff = new();
    private List<int> _hunks = new();
    private int _hunkCursor = -1;

    /// <summary>让用户在编辑器标签页里打开某个文件</summary>
    public Action<string>? OpenFileRequested { get; set; }

    public CompareWindow()
    {
        Title = "文件对比";
        Width = 1200;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;

        var root = new DockPanel { Margin = new Thickness(6) };
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);

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
        grid.Children.Add(Pane(_left, _leftLabel, "左", 0));
        grid.Children.Add(splitter);
        grid.Children.Add(Pane(_right, _rightLabel, "右", 2));
        root.Children.Add(grid);

        Content = root;

        foreach (var editor in new[] { _left, _right })
        {
            editor.ReadOnly = true;
            editor.WordWrap = false;
            editor.ConfigureDiffMarkers();
        }

        _ignoreCase.Checked += (_, _) => Rebuild();
        _ignoreCase.Unchecked += (_, _) => Rebuild();
        _ignoreTrivia.Checked += (_, _) => Rebuild();
        _ignoreTrivia.Unchecked += (_, _) => Rebuild();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F7) { MoveHunk(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        };

        UpdateStatus("选择左右两侧文件后开始对比。");
    }

    private StackPanel BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

        Button Text(string label, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => action();
            return b;
        }

        panel.Children.Add(Text("选择左侧…", () => { if (PickFile(out var p)) SetLeftFromFile(p); }));
        panel.Children.Add(Text("选择右侧…", () => { if (PickFile(out var p)) SetRightFromFile(p); }));
        panel.Children.Add(Text("重新对比", Rebuild));
        panel.Children.Add(Text("上一个差异", () => MoveHunk(-1)));
        panel.Children.Add(Text("下一个差异", () => MoveHunk(1)));
        panel.Children.Add(Text("在编辑器打开左", () => { if (_pathA.Length > 0) OpenFileRequested?.Invoke(_pathA); }));
        panel.Children.Add(Text("在编辑器打开右", () => { if (_pathB.Length > 0) OpenFileRequested?.Invoke(_pathB); }));

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

    private UIElement Pane(EditorControl editor, TextBlock label, string title, int column)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        label.Text = title + "：（未选择）";
        label.Margin = new Thickness(2, 0, 2, 2);
        label.Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush;
        DockPanel.SetDock(label, Dock.Top);
        dock.Children.Add(label);
        dock.Children.Add(editor);
        Grid.SetColumn(dock, column);
        return dock;
    }

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

    public void SetLeftFromFile(string path) => SetLeft(path, ReadText(path, out _));

    public void SetRightFromFile(string path) => SetRight(path, ReadText(path, out _));

    /// <summary>把内容放进对比槽位；path 可为空（例如「当前编辑区」）</summary>
    public void SetLeft(string path, string content)
    {
        _pathA = path;
        _linesA = SplitLines(content);
        _leftLabel.Text = "左：" + Describe(path);
        Rebuild();
    }

    public void SetRight(string path, string content)
    {
        _pathB = path;
        _linesB = SplitLines(content);
        _rightLabel.Text = "右：" + Describe(path);
        Rebuild();
    }

    private static string ReadText(string path, out int codePage)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (codePage, var bom) = EncodingService.Detect(bytes);
        return EncodingService.Decode(bytes, codePage, stripBom: true);
    }

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Describe(string path)
        => path.Length == 0 ? "编辑区内容" : $"{Path.GetFileName(path)}（{path}）";

    private void Rebuild()
    {
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

        _diff = LineDiff.Compute(_linesA, _linesB, _ignoreCase.IsChecked == true, _ignoreTrivia.IsChecked == true);
        _hunks = LineDiff.HunkStarts(_diff);
        _hunkCursor = -1;

        var textA = new List<string>(_diff.Count);
        var textB = new List<string>(_diff.Count);
        var marksA = new List<int>(_diff.Count);
        var marksB = new List<int>(_diff.Count);

        foreach (var d in _diff)
        {
            textA.Add(d.AIndex >= 0 ? _linesA[d.AIndex] : string.Empty);
            textB.Add(d.BIndex >= 0 ? _linesB[d.BIndex] : string.Empty);
            int markA = 0, markB = 0;
            switch (d.Kind)
            {
                case DiffKind.Change: markA = markB = EditorControl.ModifyMarkerIndex; break;
                case DiffKind.Delete: markA = EditorControl.DeleteMarkerIndex; break;
                case DiffKind.Insert: markB = EditorControl.WriteMarkerIndex; break;
            }
            marksA.Add(markA);
            marksB.Add(markB);
        }

        Load(_left, textA, _pathA);
        Load(_right, textB, _pathB);
        for (int i = 0; i < marksA.Count; i++) if (marksA[i] != 0) _left.MarkDiffLine(i, marksA[i]);
        for (int i = 0; i < marksB.Count; i++) if (marksB[i] != 0) _right.MarkDiffLine(i, marksB[i]);

        int changed = _diff.Count(d => d.Kind != DiffKind.Equal);
        UpdateStatus($"{_hunks.Count} 处差异段，{changed} 行不同 · 左 {_linesA.Length} 行 / 右 {_linesB.Length} 行（F7 下一个，Shift+F7 上一个）");
    }

    private void Load(EditorControl editor, List<string> lines, string path)
    {
        editor.LoadContent(string.Join("\n", lines));
        if (path.Length > 0) editor.SetLexerForFile(path);
        editor.ApplyTheme();
        editor.ConfigureDiffMarkers();
    }

    private void MoveHunk(int delta)
    {
        if (_hunks.Count == 0) return;
        _hunkCursor = Math.Clamp(_hunkCursor + delta, 0, _hunks.Count - 1);
        int line = _hunks[_hunkCursor];
        _left.GoToLine(line);
        _right.GoToLine(line);
        UpdateStatus($"差异 {_hunkCursor + 1}/{_hunks.Count}，从第 {line + 1} 行开始");
    }

    private void UpdateStatus(string message)
    {
        _status.Text = message;
        _status.Margin = new Thickness(2, 4, 2, 0);
        _status.Foreground = Application.Current.Resources["AccentBrush"] as System.Windows.Media.Brush;
    }
}
