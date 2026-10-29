using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using NotepadSharp.Controls;
using NotepadSharp.Services;

namespace NotepadSharp.Views;

/// <summary>
/// 批量查找替换（复刻 ND2 BatchFindReplace）：两个多行关键词输入区 + Keyword|Replace 表格。
/// 动作每次作用在「点击时的当前文档」上，所以窗口是非模态的，可边切标签边操作。
/// </summary>
public sealed class BatchFindReplaceWindow : Window
{
    /// <summary>ND2 tranInputKeyword 的上限：切出来的关键字超过它就拒绝刷新</summary>
    private const int MaxKeywordCount = 20000;

    private sealed class Pair
    {
        public string Keyword { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
    }

    private readonly TextBox _findEdit = MakeKeywordEdit();
    private readonly TextBox _replaceEdit = MakeKeywordEdit();
    private readonly ObservableCollection<Pair> _rows = new();
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(0, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
    };

    /// <summary>取当前文档编辑器，每次点击时重新解析（对应 ND2 autoAdjustCurrentEditWin）</summary>
    private readonly Func<EditorControl?> _getEditor;

    /// <summary>取本次标记用的颜色槽（1–5），与查找面板共用同一套轮转规则</summary>
    private readonly Func<int> _takeMarkSlot;

    public BatchFindReplaceWindow(Func<EditorControl?> getEditor, Func<int> takeMarkSlot)
    {
        _getEditor = getEditor;
        _takeMarkSlot = takeMarkSlot;

        Title = "批量查找替换";
        Width = 902;
        Height = 737;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;
        RestoreGeometry();

        var text = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;
        var root = new Grid { Margin = new Thickness(3, 6, 3, 3) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var edits = new Grid();
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edits.Children.Add(EditColumn("请输入多个查找关键字，使用空白符分隔开", _findEdit));
        var replaceColumn = EditColumn("请输入多个替换关键字，使用空白符分隔开", _replaceEdit);
        Grid.SetColumn(replaceColumn, 2);
        edits.Children.Add(replaceColumn);
        Grid.SetRow(edits, 0);
        root.Children.Add(edits);

        var table = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = text,
            RowHeaderWidth = 0,
        };
        table.Columns.Add(Column("关键词", nameof(Pair.Keyword)));
        table.Columns.Add(Column("替换", nameof(Pair.Replacement)));
        Grid.SetRow(table, 1);
        root.Children.Add(table);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AddButton(buttons, "刷新", (_, _) => Fresh());
        AddButton(buttons, "交换", (_, _) => SwapFindReplace());
        AddButton(buttons, "查找", (_, _) => BatchFind());
        AddButton(buttons, "替换", (_, _) => BatchReplace());
        AddButton(buttons, "标记", (_, _) => BatchMark());
        AddButton(buttons, "清除标记", (_, _) => ClearMark());
        AddButton(buttons, "导入", (_, _) => Import());
        AddButton(buttons, "导出", (_, _) => Export());
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        Content = root;
    }

    protected override void OnClosed(EventArgs e)
    {
        RememberGeometry();
        base.OnClosed(e);
    }

    // ==================== 表格刷新 ====================

    /// <summary>ND2 on_freshBtClick：查找区重建表格，替换区按行号填第二列</summary>
    private void Fresh()
    {
        if (_findEdit.Text.Length == 0)
        {
            Status("请输入批量查找的关键字！");
            _rows.Clear();
            return;
        }

        if (!TrySplitKeywords(_findEdit.Text, out var findWords))
        {
            _rows.Clear();
            return;
        }

        _rows.Clear();
        foreach (var word in findWords)
            _rows.Add(new Pair { Keyword = word });

        if (!TrySplitKeywords(_replaceEdit.Text, out var replaceWords)) return;

        for (int i = 0; i < replaceWords.Count; i++)
        {
            if (i < _rows.Count) _rows[i].Replacement = replaceWords[i];
            else Status($"{replaceWords[i]} 没有对应的查找匹配项！");
        }
    }

    private void SwapFindReplace()
    {
        string findText = _findEdit.Text;
        _findEdit.Text = _replaceEdit.Text;
        _replaceEdit.Text = findText;
        Fresh();
    }

    /// <summary>
    /// ND2 tranInputKeyword：先把所有空白字符换成空格再按空格切，
    /// 条数超过上限直接拒绝，空项丢弃。
    /// </summary>
    private bool TrySplitKeywords(string input, out List<string> words)
    {
        words = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (words.Count > MaxKeywordCount)
        {
            Status("最多支持 20000 条关键字！");
            words.Clear();
            return false;
        }
        return words.Count > 0;
    }

    // ==================== 三个动作 ====================

    private void BatchFind()
    {
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;

        int found = CountOccurrences(editor.GetContent(), TableKeywords());
        Status($"批量查找完成！一共发现 {found} 处。");
    }

    private void BatchReplace()
    {
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;
        if (editor.ReadOnly)
        {
            Status("只读文档不允许替换。");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        // ND2 只处理「替换词与查找词不同」的行，逐条在整篇文本里换完再一次性写回
        var pairs = new List<(string Find, string Replace)>();
        foreach (var row in _rows)
        {
            if (row.Keyword.Length == 0 || row.Keyword == row.Replacement) continue;
            pairs.Add((row.Keyword, row.Replacement));
        }
        if (pairs.Count == 0)
        {
            Status("没有需要替换的条目。");
            return;
        }

        string text = editor.GetContent();
        foreach (var (find, replace) in pairs)
            text = text.Replace(find, replace);

        editor.ReplaceWholeText(text);
        Status($"批量替换完成，一共替换 {pairs.Count} 处！");
    }

    private void BatchMark()
    {
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;
        if (editor.ReadOnly)
        {
            Status("只读文档不允许标记。");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        int slot = _takeMarkSlot();
        int marked = 0;
        foreach (var keyword in TableKeywords())
            marked += editor.MarkAll(new SearchCondition(keyword, SearchMode.Normal, true, false), slot);
        Status($"批量标记完成，一共标记 {marked} 处！");
    }

    private void ClearMark()
    {
        _getEditor()?.ClearAllMarks();
        Status("已清除全部标记。");
    }

    private EditorControl? CurrentEditor()
    {
        var editor = _getEditor();
        if (editor == null) Status("当前没有打开文档。");
        return editor;
    }

    private bool EnsureTableReady()
    {
        if (_rows.Count > 0) return true;
        Status("查找替换表为空，请先刷新！");
        return false;
    }

    private List<string> TableKeywords()
        => _rows.Where(r => r.Keyword.Length > 0).Select(r => r.Keyword).ToList();

    /// <summary>ND2 findAtBack：整篇文档逐个关键词数出现次数，字面且区分大小写</summary>
    private static int CountOccurrences(string text, IReadOnlyList<string> keywords)
    {
        int times = 0;
        foreach (var keyword in keywords)
        {
            if (keyword.Length == 0) continue;
            int index = 0;
            while (true)
            {
                index = text.IndexOf(keyword, index, StringComparison.Ordinal);
                if (index < 0) break;
                times++;
                index += keyword.Length;
            }
        }
        return times;
    }

    // ==================== 词表导入导出 ====================

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为文件 ...",
            Filter = "文本文件 (*.txt)|*.txt|所有类型 (*.*)|*.*",
            FileName = "batchfindreplace.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        var lines = new List<string> { "[BatchFindReplace]" };
        int exported = 0;
        foreach (var row in _rows)
        {
            if (row.Keyword.Length == 0) continue;
            lines.Add($"find={row.Keyword}");
            exported++;
        }
        foreach (var row in _rows)
        {
            if (row.Keyword.Length == 0) continue;
            lines.Add($"replace={row.Replacement}");
        }

        if (exported == 0)
        {
            Status("没有内容以供导出！");
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
            Status("导出配置文件成功！");
        }
        catch (Exception ex)
        {
            Status("导出失败：" + ex.Message);
        }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开",
            Filter = "文本文件 (*.txt)|*.txt|所有类型 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var findWords = new List<string>();
        var replaceWords = new List<string>();
        try
        {
            foreach (var raw in File.ReadAllLines(dlg.FileName))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1).Trim();
                if (key.Equals("find", StringComparison.OrdinalIgnoreCase)) findWords.Add(value);
                else if (key.Equals("replace", StringComparison.OrdinalIgnoreCase)) replaceWords.Add(value);
            }
        }
        catch (Exception ex)
        {
            Status("导入失败：" + ex.Message);
            return;
        }

        // ND2 把列表用空格拼回输入区，然后走一次刷新
        _findEdit.Text = string.Join(" ", findWords);
        _replaceEdit.Text = string.Join(" ", replaceWords);
        Fresh();
    }

    // ==================== 窗口大小位置记忆 ====================

    private void RestoreGeometry()
    {
        var parts = SettingsService.Current.BatchFindGeometry.Split(',');
        if (parts.Length != 4) return;
        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]))
                return;
        }
        Left = values[0];
        Top = values[1];
        Width = values[2];
        Height = values[3];
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void RememberGeometry()
    {
        SettingsService.Current.BatchFindGeometry = FormattableString.Invariant($"{Left},{Top},{Width},{Height}");
        SettingsService.Save();
    }

    // ==================== 小工具 ====================

    private static TextBox MakeKeywordEdit() => new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
    };

    private static UIElement EditColumn(string label, TextBox edit)
    {
        var panel = new DockPanel();
        var head = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
        };
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(edit);
        return panel;
    }

    private static DataGridTextColumn Column(string header, string property) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(property) { Mode = System.Windows.Data.BindingMode.TwoWay },
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
    };

    private static void AddButton(System.Windows.Controls.Panel owner, string label, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(3, 0, 3, 0),
        };
        button.Click += onClick;
        owner.Children.Add(button);
    }

    private void Status(string message) => _status.Text = message;
}
