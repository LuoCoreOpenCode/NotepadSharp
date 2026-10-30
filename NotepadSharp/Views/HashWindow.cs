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
/// <remarks>
/// 布局是纯 C# 手搭的（本窗口没有对应的 xaml）：<c>DockPanel</c> 顶贴四行
/// （来源单选 / 文件路径 / 算法勾选 / 输出选项），底贴按钮条，中间剩余区域给只读结果框。
/// 与 ND2 的差异见 功能对照.md 工具菜单一节：这里缺 MD4 与 Keccak-256（.NET 无内置实现），
/// 但多了 ND2 没有的 SHA-384 / SHA-512。
/// </remarks>
public sealed class HashWindow : Window
{
    /// <summary>
    /// 一种可选摘要算法的「名字 + 计算委托」：委托直接指向 System.Security.Cryptography 里
    /// 各算法的静态 HashData 方法，整块字节进、摘要字节出，不做流式分块。
    /// </summary>
    /// <param name="Name">结果文本里显示的算法名</param>
    /// <param name="Compute">输入字节数组、返回摘要字节数组的计算委托</param>
    private readonly record struct Algo(string Name, Func<byte[], byte[]> Compute);

    /// <summary>取编辑器当前选区文本的回调；点「计算」时才求值，保证拿到最新内容</summary>
    private readonly Func<string> _getSelection;

    /// <summary>取编辑器整篇文档文本的回调，同样按需求值</summary>
    private readonly Func<string> _getDocument;

    /// <summary>
    /// 来源单选按钮，列表顺序即来源编号：0=选中文本、1=整个文档、2=剪贴板、3=文件。
    /// TryLoadPayload 里的 switch 用的就是这个下标，两处不能各改各的。
    /// </summary>
    private readonly List<RadioButton> _sources = new();

    /// <summary>
    /// 算法勾选框，列表顺序与 Compute 里 switch 的 case 编号一一对应：
    /// 0=MD5、1=SHA-1、2=SHA-256、3=SHA-384、4=SHA-512、5=SHA3-256（系统不支持时被禁用）。
    /// </summary>
    private readonly List<CheckBox> _algos = new();

    /// <summary>
    /// 只读的等宽结果框：每行「算法名：摘要」，首行标出数据来源与数据量。
    /// 它是 DockPanel 的最后一个孩子，因此吃掉中间所有剩余空间。
    /// </summary>
    private readonly TextBox _result = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Margin = new Thickness(0, 6, 0, 6),
        MinHeight = 150,
    };

    /// <summary>输出选项：十六进制串用大写（默认勾选）；勾上 Base64 时本项不参与</summary>
    private readonly CheckBox _upperCase = new() { Content = "大写十六进制", IsChecked = true };

    /// <summary>输出选项：改出 Base64 文本而不是十六进制（Base64 本身大小写有语义，故不再套大写开关）</summary>
    private readonly CheckBox _base64 = new() { Content = "Base64 输出" };

    /// <summary>文件来源的路径显示框（只读，只能靠「选择文件…」填）；来源不是「文件」时整行隐藏</summary>
    private readonly TextBox _fileBox = new();

    /// <summary>本轮数据的描述，写进结果首行：文本来源是说明文字，文件来源是完整路径</summary>
    private string _sourceLabel = string.Empty;

    /// <summary>
    /// 本轮待摘要的字节：文本类来源统一按 UTF-8 编码，文件来源是原文件的全部字节。
    /// 每次计算前由 TryLoadPayload 重新填充，失败时保留上一轮内容但不会被使用。
    /// </summary>
    private byte[] _payload = Array.Empty<byte>();

    /// <summary>
    /// 创建哈希计算窗口。窗口不持有编辑器，只拿两个「按需取文本」的回调，
    /// 因此可以先改文档再点计算，结果始终反映最新内容。
    /// </summary>
    /// <param name="getSelection">取当前选区文本的回调，无选区时返回空串</param>
    /// <param name="getDocument">取整篇文档文本的回调，无文档时返回空串</param>
    public HashWindow(Func<string> getSelection, Func<string> getDocument)
    {
        _getSelection = getSelection;
        _getDocument = getDocument;

        // ---- 窗口基础属性：工具窗口姿态（不占任务栏、首次居中、主题底色） ----
        Title = "哈希计算 (Md5/Sha)";
        Width = 560;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;
        var text = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;

        // ---- 根布局：DockPanel，上贴四行选项、下贴按钮条，中间剩余给结果框 ----
        var root = new DockPanel { Margin = new Thickness(10) };

        // ---- 第 1 行（顶贴）：数据来源四选一，默认「选中文本」 ----
        var sources = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var label in new[] { "选中文本", "整个文档", "剪贴板", "文件" })
        {
            // 逐个建 RadioButton 并存进 _sources：列表下标就是 TryLoadPayload 里的来源编号
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

        // ---- 第 2 行（顶贴）：文件路径 + 「选择文件…」；只在选了「文件」时显示 ----
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _fileBox.Width = 400;
        _fileBox.IsReadOnly = true;         // 路径只能靠对话框填，手输容易打错也绕过存在性检查
        _fileBox.Foreground = text;
        var browse = MakeButton("选择文件…", PickFile);
        fileRow.Children.Add(_fileBox);
        fileRow.Children.Add(browse);
        DockPanel.SetDock(fileRow, Dock.Top);
        root.Children.Add(fileRow);

        // ---- 第 3 行（顶贴）：算法多选，默认勾 MD5 与 SHA-256 ----
        var algoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        algoRow.Children.Add(new TextBlock
        {
            Text = "算法：",
            Foreground = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        // 这里的数组顺序 = _algos 下标 = Compute 里 switch 的 case 编号，三处必须一起改
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

        // ---- 第 4 行（顶贴）：输出格式开关（大写十六进制 / Base64） ----
        var optionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _upperCase.Foreground = text;
        _base64.Foreground = text;
        _upperCase.Margin = new Thickness(0, 0, 12, 0);
        optionRow.Children.Add(_upperCase);
        optionRow.Children.Add(_base64);
        DockPanel.SetDock(optionRow, Dock.Top);
        root.Children.Add(optionRow);

        // ---- 底贴：动作按钮条；再底贴结果框（结果框是最后一个孩子，吃掉中间剩余空间） ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeButton("计算", Compute));
        buttons.Children.Add(MakeButton("复制结果", CopyResult));
        buttons.Children.Add(MakeButton("关闭", Close));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_result, Dock.Bottom);
        root.Children.Add(_result);

        Content = root;
        // 初始按「选中文本」来源把文件行藏起来，之后每次切来源都重算一次可见性
        UpdateSourceEnabled();
        foreach (var radio in _sources) radio.Checked += (_, _) => UpdateSourceEnabled();
    }

    /// <summary>
    /// 造一个动作按钮并把点击转交给无参委托；统一内边距与右侧间距。
    /// </summary>
    /// <param name="label">按钮文字</param>
    /// <param name="action">点击后要执行的动作（本窗口的「关闭」直接绑 <c>Window.Close</c>）</param>
    /// <returns>配好样式与事件的按钮</returns>
    private Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        // 忽略路由事件参数，只把动作本身跑一遍，调用方就能直接传方法组
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// 按当前来源刷新「文件路径行」的可见性：只有选中「文件」这一项才显示，其余情况整行收起，
    /// 免得用户对着一个用不上的输入框发呆。
    /// </summary>
    private void UpdateSourceEnabled()
    {
        _fileBox.Visibility = SelectedSource() == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 取当前选中的来源编号（即 <see cref="_sources"/> 的下标）。
    /// </summary>
    /// <returns>0=选中文本、1=整个文档、2=剪贴板、3=文件；理论上没有一项被勾选时兜底为 0</returns>
    private int SelectedSource()
    {
        for (int i = 0; i < _sources.Count; i++) if (_sources[i].IsChecked == true) return i;
        return 0;
    }

    /// <summary>
    /// 「选择文件…」：弹打开对话框，成功后把路径显示到 <c>_fileBox</c> 并自动把来源切到「文件」
    /// （勾选第四个单选钮会连带触发 Checked 事件，让文件行保持可见）。
    /// </summary>
    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要计算哈希的文件" };
        if (dlg.ShowDialog(this) != true) return;    // 用户取消就保持原状，不清空已选路径
        _fileBox.Text = dlg.FileName;
        _sources[3].IsChecked = true;
    }

    /// <summary>
    /// 探测本机是否真能用 SHA3-256：老 Windows/OpenSSL 缺失时 <c>SHA3_256.Create()</c> 会抛平台不支持异常，
    /// 这里试建一次并立刻释放，只为拿到一个布尔结论。
    /// </summary>
    /// <returns>true 表示可用；false 表示当前系统不支持（UI 里会置灰该勾选项）</returns>
    private static bool Sha3Available()
    {
        try { SHA3_256.Create().Dispose(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 「计算」：先按勾选收集算法，再取本轮数据，最后逐个算摘要并排版输出。
    /// 单个算法失败只影响它自己那一行（写明「计算失败：原因」），其余结果照常给出。
    /// </summary>
    private void Compute()
    {
        // ---- 第一步：把勾了的算法按下标映射成 Algo 记录（未勾选直接跳过） ----
        var chosen = new List<Algo>();
        for (int i = 0; i < _algos.Count; i++)
        {
            if (_algos[i].IsChecked != true) continue;
            // HashData 是静态方法组，直接当 Func<byte[], byte[]> 用，内部自己管句柄释放
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

        // ---- 第二步：按当前来源取字节；失败时把原因原样显示在结果框并中止 ----
        if (!TryLoadPayload(out string error))
        {
            _result.Text = error;
            return;
        }

        // ---- 第三步：逐个算法计算，首行先写来源与数据量，方便核对算的是哪份东西 ----
        var sb = new StringBuilder();
        sb.AppendLine($"来源：{_sourceLabel}（{FormatSize(_payload.LongLength)}）");
        foreach (var algo in chosen)
        {
            string value;
            try
            {
                // 输出格式三选一：Base64（大小写有语义，不再套大写开关）/ 大写十六进制 / 小写十六进制
                byte[] hash = algo.Compute(_payload);
                value = _base64.IsChecked == true
                    ? Convert.ToBase64String(hash)
                    : Convert.ToHexString(hash);
                if (_base64.IsChecked != true && _upperCase.IsChecked != true) value = value.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                // 兜住「算法中途不可用」之类的异常，保证其余算法的结果不丢
                value = "计算失败：" + ex.Message;
            }
            sb.AppendLine($"{algo.Name}：{value}");
        }
        // TrimEnd 去掉最后一行多余换行，复制到剪贴板时更干净
        _result.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 按当前来源把 <see cref="_payload"/> 与 <see cref="_sourceLabel"/> 准备好。
    /// 三个文本来源统一按 UTF-8 编码；文本为空视为错误（空文档和「没内容」是两回事，别默默给个摘要）。
    /// </summary>
    /// <param name="error">失败原因；成功时为空串，调用方直接把它显示到结果框</param>
    /// <returns>true 表示 _payload 已就绪可以计算；false 表示来源不可用</returns>
    private bool TryLoadPayload(out string error)
    {
        error = string.Empty;
        switch (SelectedSource())
        {
            case 0:
            {
                // 选区：由主窗口回调给当前编辑器的选中串；没选中就提示改用别的来源
                string sel = _getSelection();
                if (string.IsNullOrEmpty(sel)) { error = "当前没有选中的文本。可改用「整个文档」或「剪贴板」。"; return false; }
                _sourceLabel = "编辑器选中文本";
                _payload = Encoding.UTF8.GetBytes(sel);
                return true;
            }
            case 1:
            {
                // 整篇文档：含换行，所以同一份文件在不同换行风格下摘要会不同
                string doc = _getDocument();
                if (string.IsNullOrEmpty(doc)) { error = "当前文档是空的。"; return false; }
                _sourceLabel = "编辑器整个文档";
                _payload = Encoding.UTF8.GetBytes(doc);
                return true;
            }
            case 2:
            {
                // 剪贴板：只取文本形式；被别的进程占用等原因取不到时 GetClipboardText 返回空串
                string clip = GetClipboardText();
                if (string.IsNullOrEmpty(clip)) { error = "剪贴板里没有文本。"; return false; }
                _sourceLabel = "剪贴板文本";
                _payload = Encoding.UTF8.GetBytes(clip);
                return true;
            }
            default:
            {
                // 文件：一次读满整个文件再算（不做流式），所以超大文件会占住等量内存
                if (_fileBox.Text.Length == 0) { error = "请先「选择文件…」。"; return false; }
                try
                {
                    _payload = File.ReadAllBytes(_fileBox.Text);
                    _sourceLabel = _fileBox.Text;
                    return true;
                }
                catch (Exception ex)
                {
                    // 文件被占用/无权限/已被移动等都在此回显，路径原样保留好让用户重试
                    error = "读取文件失败：" + ex.Message;
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// 取剪贴板的纯文本。剪贴板是共享资源，随时可能被别的进程锁住而抛异常，
    /// 因此「没有文本」和「读不到文本」都归一为空串，由调用方给同一条提示。
    /// </summary>
    /// <returns>剪贴板文本；不含文本或访问失败时为空串</returns>
    private static string GetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 把字节数转成给人看的短串，用于结果首行的数据量标注。
    /// </summary>
    /// <param name="bytes">字节数（用 <c>long</c> 以便承载大文件）</param>
    /// <returns>1 KB 以下显示「N 字节」，1 MB 以下显示 KB，其余显示 MB（最多两位小数）</returns>
    private static string FormatSize(long bytes)
        => bytes switch
        {
            < 1024 => bytes + " 字节",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
            _ => (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB",
        };

    /// <summary>
    /// 「复制结果」：把整个结果框文本写进剪贴板。
    /// 结果为空什么都不做；剪贴板被占用则弹一个警告框（这里不像取用那样静默，因为用户明确要复制）。
    /// </summary>
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
