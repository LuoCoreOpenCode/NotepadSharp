using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;

namespace NotepadSharp.Views;

/// <summary>
/// 批量查找输入框（对齐 ndd 的 Batch Find）：一行一个关键词，结果交给「查找结果」面板。
/// </summary>
public sealed class BatchFindWindow : Window
{
    private readonly TextBox _terms = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 150,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
    };
    private readonly CheckBox _matchCase = new() { Content = "区分大小写" };
    private readonly CheckBox _useRegex = new() { Content = "正则表达式" };

    public IReadOnlyList<string> Terms { get; private set; } = Array.Empty<string>();

    public BatchFindWindow(string seed, bool matchCase, bool useRegex)
    {
        _matchCase.IsChecked = matchCase;
        _useRegex.IsChecked = useRegex;
        _terms.Text = seed;

        Title = "批量查找";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;
        var text = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(new TextBlock
        {
            Text = "每行一个关键词（会一次性在当前文件里查全部命中）：",
            Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(_terms);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _matchCase.Foreground = text;
        _useRegex.Foreground = text;
        _matchCase.Margin = new Thickness(0, 0, 12, 0);
        options.Children.Add(_matchCase);
        options.Children.Add(_useRegex);
        root.Children.Add(options);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "查找", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        ok.Click += (_, _) => Submit();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        // 给读屏与自动化测试一个可读的名字
        System.Windows.Automation.AutomationProperties.SetName(_terms, "关键词列表");
        _terms.Focus();
        System.Windows.Input.Keyboard.Focus(_terms);
    }

    public bool MatchCase => _matchCase.IsChecked == true;
    public bool UseRegex => _useRegex.IsChecked == true;

    private void Submit()
    {
        Terms = _terms.Text.Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        if (Terms.Count == 0) return;
        DialogResult = true;
    }
}
