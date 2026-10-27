using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotepadSharp.Controls;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace NotepadSharp.Views;

/// <summary>
/// 「列与宏 → 列编辑」：给选中的每一行在指定列插入前缀 + 文本 + 递增序号。
/// 位置可选行首、选区左边界、光标列，适合批量给多行加注释/编号/列前缀。
/// </summary>
public sealed class ColumnEditWindow : Window
{
    private readonly EditorControl _editor;
    private readonly TextBox _prefix = new();
    private readonly TextBox _text = new();
    private readonly CheckBox _number = new() { Content = "递增序号" };
    private readonly TextBox _start = new() { Text = "1", Width = 60 };
    private readonly TextBox _step = new() { Text = "1", Width = 60 };
    private readonly TextBox _digits = new() { Text = "3", Width = 60 };
    private readonly ComboBox _where = new() { Width = 130 };
    private readonly TextBlock _status = new();

    public ColumnEditWindow(EditorControl editor)
    {
        _editor = editor;
        Title = "列编辑";
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        _where.Items.Add("行首");
        _where.Items.Add("选区左边界");
        _where.Items.Add("光标列");
        _where.SelectedIndex = 1;

        AutomationProperties.SetAutomationId(_prefix, "ColPrefix");
        AutomationProperties.SetAutomationId(_text, "ColText");
        AutomationProperties.SetAutomationId(_number, "ColNumber");
        AutomationProperties.SetAutomationId(_start, "ColStart");
        AutomationProperties.SetAutomationId(_step, "ColStep");
        AutomationProperties.SetAutomationId(_digits, "ColDigits");
        AutomationProperties.SetAutomationId(_where, "ColWhere");
        AutomationProperties.SetAutomationId(_status, "ColStatus");

        _number.Checked += (_, _) => UpdateNumberInputs();
        _number.Unchecked += (_, _) => UpdateNumberInputs();

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Row("前缀", _prefix, 220, "文本", _text, 220));
        panel.Children.Add(Row("插入位置", _where, 0, "", null, 0));

        var numberRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _number.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        _number.Foreground = Application.Current.Resources["TextBrush"] as Brush;
        numberRow.Children.Add(_number);
        numberRow.Children.Add(Caption("起始", 10));
        numberRow.Children.Add(_start);
        numberRow.Children.Add(Caption("步长", 10));
        numberRow.Children.Add(_step);
        numberRow.Children.Add(Caption("位数", 10));
        numberRow.Children.Add(_digits);
        panel.Children.Add(numberRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(TextButton("应用", Apply, "ColApply"));
        buttons.Children.Add(TextButton("关闭", Close));
        panel.Children.Add(buttons);

        _status.Foreground = Application.Current.Resources["TextSecondaryBrush"] as Brush;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_status);

        Content = panel;
        UpdateNumberInputs();
        UpdateRangeHint();
        Activated += (_, _) => UpdateRangeHint();
    }

    private static TextBlock Caption(string text, double marginLeft = 0) => new()
    {
        Text = text,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Margin = new Thickness(marginLeft, 0, 6, 0),
    };

    private static Grid Row(string labelA, FrameworkElement? controlA, double widthA, string labelB, FrameworkElement? controlB, double widthB)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var a = Caption(labelA);
        Grid.SetColumn(a, 0);
        grid.Children.Add(a);
        if (controlA != null)
        {
            if (widthA > 0) controlA.Width = widthA;
            Grid.SetColumn(controlA, 1);
            grid.Children.Add(controlA);
        }
        if (controlB != null)
        {
            var b = Caption(labelB, 16);
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
            controlB.Width = widthB;
            Grid.SetColumn(controlB, 3);
            grid.Children.Add(controlB);
        }
        return grid;
    }

    private Button TextButton(string label, Action action, string? automationId = null)
    {
        var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 70 };
        if (automationId != null) AutomationProperties.SetAutomationId(b, automationId);
        b.Click += (_, _) => action();
        return b;
    }

    private void UpdateNumberInputs()
    {
        bool on = _number.IsChecked == true;
        _start.IsEnabled = _step.IsEnabled = _digits.IsEnabled = on;
    }

    private void UpdateRangeHint()
    {
        var (startLine, endLine, startCol, caretCol) = _editor.GetColumnEditContext();
        _status.Text = $"{RangeText(startLine, endLine)}，" +
                       $"左边界第 {Math.Min(startCol, caretCol) + 1} 列，光标第 {caretCol + 1} 列";
    }

    private static string RangeText(int startLine, int endLine)
        => startLine == endLine
            ? $"当前选区：第 {startLine + 1} 行"
            : $"当前选区：第 {startLine + 1}–{endLine + 1} 行（{endLine - startLine + 1} 行）";

    private void Apply()
    {
        var (startLine, endLine, startCol, caretCol) = _editor.GetColumnEditContext();
        int column = _where.SelectedIndex switch
        {
            0 => 0,
            1 => Math.Min(startCol, caretCol),
            _ => caretCol,
        };

        bool numbering = _number.IsChecked == true;
        string prefix = _prefix.Text, text = _text.Text;
        if (!numbering && prefix.Length == 0 && text.Length == 0)
        {
            _status.Text = "没有要插入的内容：前缀、文本、序号至少填一项。";
            return;
        }

        int start = 1, step = 1, digits = 0;
        if (numbering
            && (!int.TryParse(_start.Text, out start) || !int.TryParse(_step.Text, out step)
                || !int.TryParse(_digits.Text, out digits) || digits is < 0 or > 9))
        {
            MessageBox.Show(this, "序号的起始 / 步长 / 位数要填整数（位数 0–9）。", "列编辑",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int count = endLine - startLine + 1;
        _editor.BeginEditGroup();
        try
        {
            for (int line = endLine; line >= startLine; line--)
            {
                string number = numbering
                    ? (start + (line - startLine) * step).ToString(digits > 0 ? "D" + digits : null)
                    : string.Empty;
                _editor.InsertColumnText(line, column, prefix + number + text);
            }
        }
        finally
        {
            _editor.EndEditGroup();
        }

        _editor.FocusEditor();
        _status.Text = $"已插入 {count} 行（第 {startLine + 1}–{endLine + 1} 行，第 {column + 1} 列），Ctrl+Z 可整体撤销";
    }
}
