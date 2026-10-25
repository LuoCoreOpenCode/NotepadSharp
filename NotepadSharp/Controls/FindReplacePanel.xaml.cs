using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NotepadSharp.Controls;

/// <summary>
/// 查找替换面板
/// </summary>
public partial class FindReplacePanel : UserControl
{
    public FindReplacePanel()
    {
        InitializeComponent();
    }

    public event EventHandler<FindReplaceArgs>? FindRequested;
    public event EventHandler<FindReplaceArgs>? ReplaceRequested;
    public event EventHandler<FindReplaceArgs>? ReplaceAllRequested;
    public event EventHandler? FindNextRequested;
    public event EventHandler? FindPreviousRequested;
    public event EventHandler? CloseRequested;

    public class FindReplaceArgs
    {
        public string FindText { get; set; } = string.Empty;
        public string ReplaceText { get; set; } = string.Empty;
        public bool MatchCase { get; set; }
        public bool MatchWord { get; set; }
        public bool Regex { get; set; }
    }

    private FindReplaceArgs GetArgs()
    {
        return new FindReplaceArgs
        {
            FindText = FindTextBox.Text ?? string.Empty,
            ReplaceText = ReplaceTextBox.Text ?? string.Empty,
            MatchCase = MatchCaseCheckBox.IsChecked == true,
            MatchWord = MatchWordCheckBox.IsChecked == true,
            Regex = RegexCheckBox.IsChecked == true,
        };
    }

    public void SetFindText(string text)
    {
        FindTextBox.Text = text;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    /// <summary>
    /// 供外部获取当前查找参数（不触发事件）
    /// </summary>
    public FindReplaceArgs? GetArgsPublic()
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return null;
        return GetArgs();
    }

    public void SetMatchCount(int count)
    {
        MatchCountText.Text = count > 0 ? $"{count} 处匹配" : string.Empty;
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        var args = GetArgs();
        _lastArgs = args;
        FindNextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFindPrevious(object sender, RoutedEventArgs e)
    {
        var args = GetArgs();
        _lastArgs = args;
        FindPreviousRequested?.Invoke(this, EventArgs.Empty);
    }

    private FindReplaceArgs? _lastArgs;

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        ReplaceRequested?.Invoke(this, GetArgs());
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        ReplaceAllRequested?.Invoke(this, GetArgs());
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var args = GetArgs();
            _lastArgs = args;
            FindRequested?.Invoke(this, args);
            e.Handled = true;
        }
    }
}
