using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Clipboard = System.Windows.Clipboard;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace NotepadSharp.Views;

/// <summary>
/// 哈希计算窗口：取剪贴板/选中/文件，算多种摘要。
/// .NET 内置的摘要算法有限，这里提供 SHA-1/256/384/512 与 SHA3-256（平台支持时）。
/// </summary>
public sealed class HashWindow : Window
{
    private readonly record struct Algo(string Name, Func<byte[], byte[]> Compute);

    private readonly Func<string> _getSelection;
    private readonly Func<string> _getDocument;
    private readonly List<RadioButton> _sources = new();
    private readonly List<CheckBox> _algos = new();
    private readonly TextBox _result = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Margin = new Thickness(0, 6, 0, 6),
        MinHeight = 150,
    };
    private readonly CheckBox _upperCase = new() { Content = "大写十六进制", IsChecked = true };
    private readonly CheckBox _base64 = new() { Content = "Base64 输出" };
    private readonly TextBox _fileBox = new();
    private string _sourceLabel = string.Empty;
    private byte[] _payload = Array.Empty<byte>();

    public HashWindow(Func<string> getSelection, Func<string> getDocument)
    {
        _getSelection = getSelection;
        _getDocument = getDocument;

        Title = "哈希计算 (Md5/Sha)";
        Width = 560;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;
        var text = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;

        var root = new DockPanel { Margin = new Thickness(10) };

        var sources = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var label in new[] { "选中文本", "整个文档", "剪贴板", "文件" })
        {
            var radio = new RadioButton
            {
                Content = label,
                Margin = new Thickness(0, 0, 14, 0),
                Foreground = text,
                IsChecked = label == "选中文本",
            };
            _sources.Add(radio);
            sources.Children.Add(radio);
        }
        DockPanel.SetDock(sources, Dock.Top);
        root.Children.Add(sources);

        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _fileBox.Width = 400;
        _fileBox.IsReadOnly = true;
        _fileBox.Foreground = text;
        var browse = MakeButton("选择文件…", PickFile);
        fileRow.Children.Add(_fileBox);
        fileRow.Children.Add(browse);
        DockPanel.SetDock(fileRow, Dock.Top);
        root.Children.Add(fileRow);

        var algoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        algoRow.Children.Add(new TextBlock
        {
            Text = "算法：",
            Foreground = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        foreach (var name in new[] { "MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512", "SHA3-256" })
        {
            var box = new CheckBox
            {
                Content = name,
                Margin = new Thickness(0, 0, 12, 0),
                Foreground = text,
                IsChecked = name is "MD5" or "SHA-256",
            };
            // SHA3 需要 OpenSSL 3 / 较新系统，创建失败就置灰而不是等用户点了才报错
            if (name == "SHA3-256" && !Sha3Available())
            {
                box.IsEnabled = false;
                box.Content = "SHA3-256（系统不支持）";
            }
            _algos.Add(box);
            algoRow.Children.Add(box);
        }
        DockPanel.SetDock(algoRow, Dock.Top);
        root.Children.Add(algoRow);

        var optionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _upperCase.Foreground = text;
        _base64.Foreground = text;
        _upperCase.Margin = new Thickness(0, 0, 12, 0);
        optionRow.Children.Add(_upperCase);
        optionRow.Children.Add(_base64);
        DockPanel.SetDock(optionRow, Dock.Top);
        root.Children.Add(optionRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeButton("计算", Compute));
        buttons.Children.Add(MakeButton("复制结果", CopyResult));
        buttons.Children.Add(MakeButton("关闭", Close));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_result, Dock.Bottom);
        root.Children.Add(_result);

        Content = root;
        UpdateSourceEnabled();
        foreach (var radio in _sources) radio.Checked += (_, _) => UpdateSourceEnabled();
    }

    private Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private void UpdateSourceEnabled()
    {
        _fileBox.Visibility = SelectedSource() == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int SelectedSource()
    {
        for (int i = 0; i < _sources.Count; i++) if (_sources[i].IsChecked == true) return i;
        return 0;
    }

    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要计算哈希的文件" };
        if (dlg.ShowDialog(this) != true) return;
        _fileBox.Text = dlg.FileName;
        _sources[3].IsChecked = true;
    }

    private static bool Sha3Available()
    {
        try { SHA3_256.Create().Dispose(); return true; }
        catch { return false; }
    }

    private void Compute()
    {
        var chosen = new List<Algo>();
        for (int i = 0; i < _algos.Count; i++)
        {
            if (_algos[i].IsChecked != true) continue;
            switch (i)
            {
                case 0: chosen.Add(new("MD5", MD5.HashData)); break;
                case 1: chosen.Add(new("SHA-1", SHA1.HashData)); break;
                case 2: chosen.Add(new("SHA-256", SHA256.HashData)); break;
                case 3: chosen.Add(new("SHA-384", SHA384.HashData)); break;
                case 4: chosen.Add(new("SHA-512", SHA512.HashData)); break;
                case 5: chosen.Add(new("SHA3-256", SHA3_256.HashData)); break;
            }
        }
        if (chosen.Count == 0)
        {
            _result.Text = "请至少勾选一种算法。";
            return;
        }

        if (!TryLoadPayload(out string error))
        {
            _result.Text = error;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"来源：{_sourceLabel}（{FormatSize(_payload.LongLength)}）");
        foreach (var algo in chosen)
        {
            string value;
            try
            {
                byte[] hash = algo.Compute(_payload);
                value = _base64.IsChecked == true
                    ? Convert.ToBase64String(hash)
                    : Convert.ToHexString(hash);
                if (_base64.IsChecked != true && _upperCase.IsChecked != true) value = value.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                value = "计算失败：" + ex.Message;
            }
            sb.AppendLine($"{algo.Name}：{value}");
        }
        _result.Text = sb.ToString().TrimEnd();
    }

    private bool TryLoadPayload(out string error)
    {
        error = string.Empty;
        switch (SelectedSource())
        {
            case 0:
            {
                string sel = _getSelection();
                if (string.IsNullOrEmpty(sel)) { error = "当前没有选中的文本。可改用「整个文档」或「剪贴板」。"; return false; }
                _sourceLabel = "编辑器选中文本";
                _payload = Encoding.UTF8.GetBytes(sel);
                return true;
            }
            case 1:
            {
                string doc = _getDocument();
                if (string.IsNullOrEmpty(doc)) { error = "当前文档是空的。"; return false; }
                _sourceLabel = "编辑器整个文档";
                _payload = Encoding.UTF8.GetBytes(doc);
                return true;
            }
            case 2:
            {
                string clip = GetClipboardText();
                if (string.IsNullOrEmpty(clip)) { error = "剪贴板里没有文本。"; return false; }
                _sourceLabel = "剪贴板文本";
                _payload = Encoding.UTF8.GetBytes(clip);
                return true;
            }
            default:
            {
                if (_fileBox.Text.Length == 0) { error = "请先「选择文件…」。"; return false; }
                try
                {
                    _payload = File.ReadAllBytes(_fileBox.Text);
                    _sourceLabel = _fileBox.Text;
                    return true;
                }
                catch (Exception ex)
                {
                    error = "读取文件失败：" + ex.Message;
                    return false;
                }
            }
        }
    }

    private static string GetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }

    private static string FormatSize(long bytes)
        => bytes switch
        {
            < 1024 => bytes + " 字节",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
            _ => (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB",
        };

    private void CopyResult()
    {
        if (_result.Text.Length == 0) return;
        try { Clipboard.SetText(_result.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
