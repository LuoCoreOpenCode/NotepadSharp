using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NotepadSharp.Controls;
using NotepadSharp.Services;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WpfAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Views;

/// <summary>
/// 列编辑器对话框（对齐 ndd 的 columnedit：插入文本 / 复制首行 / 粘贴剪贴板 / 递增编号）。
/// </summary>
public sealed class ColumnEditorWindow : Window
{
    private readonly StackPanel _modePanel = new();
    private readonly List<RadioButton> _modes = new();
    private readonly TextBox _text = new();
    private readonly TextBox _initial = new() { Text = "1" };
    private readonly TextBox _increment = new() { Text = "1" };
    private readonly TextBox _repeat = new() { Text = "1" };
    private readonly TextBox _digits = new() { Text = "0" };
    private readonly TextBox _prefix = new();
    private readonly CheckBox _leadingZero = new() { Content = "前导零补齐", IsChecked = true };
    private readonly ComboBox _radix = new();

    public int Mode { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public int Initial { get; private set; } = 1;
    public int Increment { get; private set; } = 1;
    public int Repeat { get; private set; } = 1;
    public int Digits { get; private set; }
    public EditorControl.ColumnNumberFormat NumberFormat { get; private set; }
    public bool LeadingZero { get; private set; } = true;
    public string Prefix { get; private set; } = string.Empty;

    public ColumnEditorWindow()
    {
        Title = "列编辑器";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;

        foreach (var label in new[] { "插入文本", "复制首行到各行", "粘贴剪贴板到各行", "插入递增编号" })
        {
            var radio = new RadioButton
            {
                Content = label,
                Margin = new Thickness(0, 2, 12, 2),
                Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
            };
            radio.Checked += (_, _) => UpdateEnabled();
            _modes.Add(radio);
            _modePanel.Children.Add(radio);
        }
        _modes[0].IsChecked = true;

        foreach (var (label, fmt) in new[]
        {
            ("十进制", EditorControl.ColumnNumberFormat.Decimal),
            ("十六进制（大写）", EditorControl.ColumnNumberFormat.HexUpper),
            ("十六进制（小写）", EditorControl.ColumnNumberFormat.HexLower),
            ("八进制", EditorControl.ColumnNumberFormat.Octal),
            ("二进制", EditorControl.ColumnNumberFormat.Binary),
        })
        {
            _radix.Items.Add(new ComboBoxItem { Content = label, Tag = fmt });
        }
        _radix.SelectedIndex = 0;

        var grid = new Grid { Margin = new Thickness(14, 10, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int row = 0;
        AddRow(grid, "方式", row, _modePanel); row++;
        AddRow(grid, "插入文本", row, _text); row++;
        AddRow(grid, "编号格式", row, _radix); row++;
        AddRow(grid, "初始值 / 步长", row, Pair(_initial, _increment)); row++;
        AddRow(grid, "重复次数 / 位数", row, Pair(_repeat, _digits)); row++;
        AddRow(grid, "前缀", row, _prefix); row++;
        AddRow(grid, null, row, _leadingZero); row++;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = WpfAlignment.Right,
            Margin = new Thickness(0, 14, 14, 12),
        };
        var ok = new Button { Content = "确定", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 76, IsCancel = true };
        ok.Click += OnOk;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(buttons);
        Content = root;

        RestoreLastUsed();
        UpdateEnabled();
        _text.Focus();
    }

    private int SelectedMode => _modes.FindIndex(r => r.IsChecked == true);

    private static void AddRow(Grid grid, string? label, int row, UIElement element)
    {
        if (label != null)
        {
            var text = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 5, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
        }

        Grid.SetRow(element, row);
        Grid.SetColumn(element, 1);
        grid.Children.Add(element);
    }

    private static StackPanel Pair(TextBox left, TextBox right)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        left.Margin = new Thickness(0, 3, 8, 3);
        right.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(left);
        panel.Children.Add(right);
        return panel;
    }

    private void UpdateEnabled()
    {
        int mode = SelectedMode;
        _text.IsEnabled = mode == 0;
        _radix.IsEnabled = mode == 3;
        _initial.IsEnabled = mode == 3;
        _increment.IsEnabled = mode == 3;
        _repeat.IsEnabled = mode == 3;
        _digits.IsEnabled = mode == 3;
        _prefix.IsEnabled = mode == 3;
        _leadingZero.IsEnabled = mode == 3;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Mode = Math.Max(0, SelectedMode);
        Text = _text.Text;
        Prefix = _prefix.Text;
        LeadingZero = _leadingZero.IsChecked == true;

        if (Mode == 3)
        {
            bool okNumbers = TryParseInt(_initial.Text, out int initial);
            okNumbers &= TryParseInt(_increment.Text, out int increment);
            okNumbers &= TryParseInt(_repeat.Text, out int repeat);
            okNumbers &= TryParseInt(_digits.Text, out int digits);
            if (!okNumbers)
            {
                MessageBox.Show(this, "初始值 / 步长 / 重复次数 / 位数必须是整数。", "列编辑器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Initial = initial;
            Increment = increment;
            Repeat = repeat;
            Digits = digits;
        }
        else
        {
            Initial = 1;
            Increment = 1;
            Repeat = 1;
            Digits = 0;
        }

        NumberFormat = (_radix.SelectedItem as ComboBoxItem)?.Tag is EditorControl.ColumnNumberFormat fmt
            ? fmt
            : EditorControl.ColumnNumberFormat.Decimal;

        SettingsService.Current.ColumnEditorLastUsed = ToSettingsString();
        SettingsService.Save();
        DialogResult = true;
    }

    private static bool TryParseInt(string s, out int value)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public string ToSettingsString()
        => string.Join('|', Mode, Text, Initial, Increment, Repeat, Digits, (int)NumberFormat, LeadingZero ? 1 : 0, Prefix);

    private void RestoreLastUsed()
    {
        string saved = SettingsService.Current.ColumnEditorLastUsed;
        if (string.IsNullOrEmpty(saved)) return;

        var parts = saved.Split('|', 9);
        if (parts.Length < 9) return;

        if (int.TryParse(parts[0], out int mode) && mode >= 0 && mode < _modes.Count) _modes[mode].IsChecked = true;
        _text.Text = parts[1];
        _initial.Text = parts[2];
        _increment.Text = parts[3];
        _repeat.Text = parts[4];
        _digits.Text = parts[5];
        if (int.TryParse(parts[6], out int fmt) && fmt >= 0 && fmt < _radix.Items.Count) _radix.SelectedIndex = fmt;
        _leadingZero.IsChecked = parts[7] == "1";
        _prefix.Text = parts[8];
    }
}
