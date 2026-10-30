using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
// 项目同时开了 UseWindowsForms，这两个类型不加别名会撞名
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NotepadSharp.Services;

/// <summary>
/// 走 GitHub Releases 的自动更新。清单和安装包都用「最新 Release 的固定附件直链」：
/// https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/&lt;asset&gt;
/// 这个地址匿名可访问、永远指向最新一版，所以不需要查 API，也不用把下载地址写进代码。
/// 清单 latest.json 由打包流水线（.github/workflows/release.yml）作为附件一起传上去。
/// 两个入口都在 MainWindow：启动后延迟 5 秒的静默检查（showUpToDateDialog=false）
/// 和「帮助 → 检查更新」的手动触发（true）；进度文本走状态栏回调，本类不直接碰任何控件。
/// 全类无实例状态，静态方法可被任意线程调用，重入由 _checkRunning 一个标志挡住。
/// </summary>
public static class UpdateService
{
    /// <summary>GitHub 仓库属主：换仓库时只需改这一处，下载根地址全部由它拼出来</summary>
    public const string GithubOwner = "LuoCoreOpenCode";

    /// <summary>GitHub 仓库名：同上，决定 releases/latest/download 直链里的路径段</summary>
    public const string GithubRepo = "NotepadSharp";

    /// <summary>
    /// 清单和安装包所在仓库的下载根地址
    /// /releases/latest 是 GitHub 的固定别名，永远 302 到「最近一次已发布的 Release」，
    /// 因此客户端不需要调 API 查 Release 列表，也不用把每版下载地址写死进代码。
    /// </summary>
    private static string ReleaseDownloadRoot =>
        $"https://github.com/{GithubOwner}/{GithubRepo}/releases/latest/download";

    /// <summary>
    /// 清单是个小 json，20 秒拿不到就当网络不通，别让用户干等
    /// 只作用在 LoadManifestAsync 的 CancellationTokenSource 上；安装包下载用 Http 的总超时，两者互不影响。
    /// </summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 自包含安装包有几十 MB，GitHub CDN 在国内实测只有 20~40 KB/s，给足 90 分钟
    /// 全类共用这一个实例：HttpClient 每次 new 都会留下一批处于 TIME_WAIT 的 socket，
    /// 静态复用才符合官方建议。附件直链落在这台公开 CDN 上，匿名可下，不必自定义 User-Agent 或令牌。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(90) };

    /// <summary>
    /// 「检查更新正在进行」的标志：0=空闲，1=正在跑。
    /// 用 int 而不是 bool，是为了能被 Interlocked.CompareExchange 原子地抢占，
    /// 免得用户连点两次「检查更新」后两条下载流往同一个临时文件里写。
    /// </summary>
    private static int _checkRunning;

    /// <summary>
    /// latest.json 的一份清单快照，四个字段对应清单里的同名键（读取时大小写不敏感，缺键按空串处理）。
    /// File 既可以只是附件文件名（按最新 Release 的附件拼直链），也可以写成完整 URL 指向别的下载源。
    /// Sha256 是安装包的小写十六进制摘要，空串表示这版清单没给校验值、跳过校验。
    /// </summary>
    /// <param name="Version">新版本号，形如 1.0.1，与 CurrentVersion 做点分十进制比较</param>
    /// <param name="File">安装包文件名或完整下载地址</param>
    /// <param name="Sha256">安装包 sha256（十六进制），用于下载后完整性校验</param>
    /// <param name="Notes">更新说明，原样拼进「发现新版本」的询问框</param>
    private sealed record UpdateManifest(string Version, string File, string Sha256, string Notes);

    /// <summary>
    /// 当前程序版本号，也是「关于」对话框的唯一取源（单一事实源，别再各处写死版本串）。
    /// 取的是 InformationalVersion（由 csproj 的 Version 属性推导，打包流水线也按同一版本出清单），拿不到才退回 AssemblyVersion。
    /// 形如 "1.0.0+abc123" 的构建元数据会被裁掉，保证和清单里的 "1.0.1" 能按段比较。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var info = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // 兜底：没标 InformationalVersion（非 SDK 生成的程序集）时退回 AssemblyVersion，再不行走下面的 "0.0.0"
            if (string.IsNullOrWhiteSpace(info)) info = typeof(UpdateService).Assembly.GetName().Version?.ToString();
            // InformationalVersion 形如 "1.0.0+abc123"，构建元数据不参与比较
            var plus = info?.IndexOf('+') ?? -1;
            // 两段都拿不到时给 "0.0.0"：宁可显示成最老的版本，也不要 null 传进比较函数再解引用
            return plus > 0 ? info![..plus] : (info ?? "0.0.0");
        }
    }

    /// <summary>
    /// 检查并（经用户确认后）安装更新。showUpToDateDialog=false 用于启动时的静默检查：
    /// 没有新版本或网络不通时完全不打扰用户，只在确实有更新时才弹窗。
    /// 整个流程是「取清单 → 比版本 → 询问 → 下载并校验 → 启动安装程序 → 自杀让位」，
    /// 任何一步失败都在这里就地结束，只有最后一步成功才会关掉应用。
    /// </summary>
    /// <param name="owner">所有对话框的属主窗口（MainWindow），保证弹框跟着主窗而不是浮在别处</param>
    /// <param name="showUpToDateDialog">true=手动触发（已是最新/失败也要出声），false=启动静默检查</param>
    /// <param name="progress">下载进度回调，文本可直接写状态栏；可为 null</param>
    public static async Task CheckForUpdatesAsync(Window owner, bool showUpToDateDialog, Action<string>? progress = null)
    {
        // 下载期间界面不阻塞，用户能再点一次「检查更新」；两份安装包并排写同一个临时文件会互相踩
        if (Interlocked.CompareExchange(ref _checkRunning, 1, 0) != 0) return;
        try
        {
            await CheckCoreAsync(owner, showUpToDateDialog, progress);
        }
        finally
        {
            // 放 finally：网络异常、用户在询问框点否、校验失败都要把标志清干净，否则下次永远进不来
            Interlocked.Exchange(ref _checkRunning, 0);
        }
    }

    /// <summary>
    /// CheckForUpdatesAsync 的实体流程（外层只负责抢占/释放防重入标志）。
    /// 每一步都单独 try/catch：失败只报当前步骤的原因，不把「下载失败」说成「检查失败」。
    /// </summary>
    /// <param name="owner">对话框属主窗口</param>
    /// <param name="showUpToDateDialog">是否需要在「已是最新/检查失败」时弹框告知</param>
    /// <param name="progress">下载进度回调，可为 null</param>
    private static async Task CheckCoreAsync(Window owner, bool showUpToDateDialog, Action<string>? progress)
    {
        UpdateManifest? manifest;
        try
        {
            // 这里刻意不用 ConfigureAwait(false)：await 之后还要弹框、启动进程、关程序，
            // 切到线程池线程会在 MessageBox.Show(owner, …) 上抛跨线程异常
            manifest = await LoadManifestAsync();
        }
        catch (Exception ex)
        {
            // 静默检查时断网/仓库没发版都是常态，不弹框；手动检查才把失败原因摊给用户看
            if (showUpToDateDialog)
                MessageBox.Show(owner, $"检查更新失败：{ex.Message}", "NotepadSharp 更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 清单缺失（内容为空）或版本不比当前新，都算「已是最新」
        if (manifest == null || !IsNewer(manifest.Version, CurrentVersion))
        {
            if (showUpToDateDialog)
                MessageBox.Show(owner, $"当前已是最新版本（{CurrentVersion}）。", "NotepadSharp 更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 说明为空就不额外加空行，避免询问框里出现一段突兀的空白
        var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? string.Empty : "\n\n" + manifest.Notes;
        var ask = MessageBox.Show(owner,
            $"发现新版本 {manifest.Version}（当前 {CurrentVersion}）。{notes}\n\n" +
            "下载并安装吗？安装程序会替换当前版本并自动重启程序。",
            "NotepadSharp 更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask != MessageBoxResult.Yes) return;

        string setupPath;
        try
        {
            // 下载在池线程上推进，而 progress 直接写状态栏控件，必须先回 UI 线程
            Action<string> report = text => owner.Dispatcher.BeginInvoke(new Action(() => progress?.Invoke(text)));
            setupPath = await DownloadAsync(manifest, report);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"下载更新失败：{ex.Message}", "NotepadSharp 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // UseShellExecute=true 才能按文件关联交给系统起进程（安装程序需要 UAC 时也靠它正常弹授权框）
            Process.Start(new ProcessStartInfo(setupPath, InstallerArguments)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // 起不来说明临时包还在，把路径告诉用户，别让几十 MB 白下
            MessageBox.Show(owner, $"启动安装程序失败：{ex.Message}\n\n已下载到 {setupPath}，可手动运行。",
                "NotepadSharp 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 主动退出释放文件占用，安装程序（/RESTARTAPPLICATIONS）装完再把程序拉起来
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 静默装完要能把程序拉回来，所以不能只走管理员模式。
    /// 当前实例装在 %LOCALAPPDATA%\Programs 下说明上次是「仅当前用户」安装，
    /// 更新时得带 /CURRENTUSER，否则会弹 UAC 把静默流程卡死。
    /// 后半段 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPPLICATIONS 是 Inno Setup 的标准静默参数，
    /// 与 packaging/NotepadSharp.iss 里的 RestartApplications=yes 配套：前者让装完自动重启应用。
    /// </summary>
    private static string InstallerArguments
    {
        get
        {
            var perUserRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            var perUser = BaseDirectoryStartsWith(perUserRoot);
            return (perUser ? "/CURRENTUSER " : "/ADMIN ") + "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPPLICATIONS";
        }
    }

    /// <summary>
    /// 判断当前程序的安装目录是否落在指定前缀之下（用前缀判断「装在哪」，比读注册表更直接且不依赖卸载信息）。
    /// </summary>
    /// <param name="prefix">目录前缀，如 %LOCALAPPDATA%\Programs；不带结尾分隔符也能正确匹配</param>
    /// <returns>安装根目录以前缀开头则为 true</returns>
    private static bool BaseDirectoryStartsWith(string prefix)
    {
        // 去掉结尾的 \ 再比，避免 "…\Programs\NotepadSharp\" 与 "...Programs" 这种带/不带尾斜杠的错配；
        // Windows 路径大小写不敏感，所以用 OrdinalIgnoreCase 而不是当前区域的大小写规则
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取并解析更新清单 latest.json。全程只读文本，不碰 UI，因此可以安心 ConfigureAwait(false)。
    /// </summary>
    /// <returns>清单对象；json 里没有这四个键时各字段为空串（调用方按「版本不比当前新」处理）</returns>
    private static async Task<UpdateManifest?> LoadManifestAsync()
    {
        // 留一个环境变量口子：内部试用时可以指向自己的镜像源或本地测试服务
        var overrideUrl = Environment.GetEnvironmentVariable("NOTEPADSHARP_UPDATE_MANIFEST");
        var url = string.IsNullOrWhiteSpace(overrideUrl)
            ? $"{ReleaseDownloadRoot}/latest.json"
            : overrideUrl;
        // 单独的短超时：清单只有几 KB，20 秒拿不到就当网络不通（不能跟着 Http 的 90 分钟一起等）
        using var cts = new CancellationTokenSource(ManifestTimeout);
        var json = await Http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        // 清单必须是无 BOM 的 UTF-8（build.ps1 已按无 BOM 写）：JsonDocument.Parse 收到带 BOM 的字符串会直接抛
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new UpdateManifest(
            ReadString(root, "version"),
            ReadString(root, "file"),
            ReadString(root, "sha256"),
            ReadString(root, "notes"));
    }

    /// <summary>
    /// 从 JSON 对象里按名字取值，键名大小写不敏感，缺失或类型不对都退化成空串。
    /// 手写枚举而不用 JsonSerializer 反序列化，是为了让「清单少给一个键」不至于整份解析失败。
    /// </summary>
    /// <param name="obj">要查找的 JSON 对象元素</param>
    /// <param name="name">键名，如 version / file / sha256 / notes</param>
    /// <returns>字符串值；键不存在、不是字符串或值为 null 时返回空串</returns>
    private static string ReadString(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? string.Empty) : string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 把安装包下到系统临时目录并做 sha256 校验，全程流式写盘、按百分比+时间双节流报进度。
    /// 下载中途抛出的异常（超时 / 断流 / HTTP 非 2xx）直接向上冒，由调用方统一提示「下载更新失败」。
    /// </summary>
    /// <param name="manifest">已确认要装的清单，提供下载地址、文件名、校验值、版本号</param>
    /// <param name="progress">进度文本回调，已确保在 UI 线程上被调用；可为 null</param>
    /// <returns>下载完成并通过校验的本地完整路径，可直接交给安装程序</returns>
    private static async Task<string> DownloadAsync(UpdateManifest manifest, Action<string>? progress)
    {
        // file 写成完整 URL 时直接用它（指向别的下载源），否则按「最新 Release 的附件」拼
        var url = manifest.File.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? manifest.File
            : $"{ReleaseDownloadRoot}/{manifest.File}";
        var target = Path.Combine(Path.GetTempPath(), Path.GetFileName(manifest.File));
        // 上次中断留下的半成品会让下面 CreateNew 直接抛「文件已存在」，先清掉再写
        if (File.Exists(target)) File.Delete(target);

        // ResponseHeadersRead：拿到响应头就能读 Content-Length，边下边报进度，不用等整包进内存
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        // 非 2xx（比如 Release 被删、附件不存在）在这里转成异常，由上层归到「下载失败」
        response.EnsureSuccessStatusCode();
        // 服务器没给 Content-Length 时用 -1 表示「总量未知」，下面的百分比分支会自动跳过
        var total = response.Content.Headers.ContentLength ?? -1L;

        const int reportEveryPercent = 5;
        // 初值取负，保证第一次进循环就能报一次 0% 左右的进度，而不是干等到 5%
        var lastReported = -reportEveryPercent;
        long done = 0;
        // 全程平均速度比瞬时速度稳，算出来的剩余时间不会来回跳
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        // 两个 await using 串成链：响应流和文件流都在异常路径上自动释放，
        // FileStream 用完必须关掉，否则安装程序还没起、文件句柄先把自己锁住了
        await using (var http = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await http.ReadAsync(buffer).ConfigureAwait(false);
                // 读到 0 字节 = 服务端正常收尾，退出循环（不是错误）
                if (read == 0) break;
                // AsMemory(0, read)：只写真正读到的那段，最后一块通常不满 64 KB
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    // 自包含包 5% 就是好几 MB，慢网络下只按百分比节流的话进度条会很久不动，
                    // 所以再加一层时间节流：最多隔 2 秒就刷一次，让剩余时间一直有反馈
                    if (percent - lastReported >= reportEveryPercent || percent == 100
                        || watch.Elapsed - lastReport >= TimeSpan.FromSeconds(2))
                    {
                        lastReported = percent;
                        lastReport = watch.Elapsed;
                        progress?.Invoke(
                            $"正在下载 {manifest.Version} 安装包 … {percent}%（{HumanSize(done)} / {HumanSize(total)}）" +
                            HumanEta(watch.Elapsed.TotalSeconds, done, total));
                    }
                }
            }
        }

        // 校验：清单没给 sha256 就跳过（打包流水线漏传时的兜底，宁可少一层保险也不要卡住更新）
        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            await using var check = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装包校验失败（sha256 不匹配），已放弃安装。");
        }

        return target;
    }

    /// <summary>
    /// 字节数转成人类可读单位（B/KB/MB/GB），最多除到 GB 不再往上，避免出现 TB 这种预览里少见的写法。
    /// </summary>
    /// <param name="bytes">字节数</param>
    /// <returns>如 "58.4 MB"；整数单位（B）保留原值不带小数</returns>
    private static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        // "0.#" 让 58.0 MB 显示成 58 MB；B 档直接用原始整数，不显示四舍五入后的小数
        return i == 0 ? $"{bytes} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    /// <summary>
    /// 按全程平均速度估算剩余时间；刚开下时速度还没成形（不到 1 秒），返回空串不显示。
    /// 只回一段「 · 速度 · 剩余时间」文本，由调用方拼在进度串后面。
    /// </summary>
    /// <param name="elapsedSeconds">已下载耗时（秒）</param>
    /// <param name="done">已下载字节数</param>
    /// <param name="total">总字节数，调用方保证 &gt; 0 才会走到这里</param>
    /// <returns>形如 " · 32 KB/s · 剩余约 12 分 05 秒" 的尾巴，或空串</returns>
    private static string HumanEta(double elapsedSeconds, long done, long total)
    {
        if (elapsedSeconds < 1 || done <= 0) return string.Empty;
        var speed = done / elapsedSeconds;                       // 字节/秒
        var remainSeconds = (total - done) / speed;
        return $" · {HumanSpeed(speed)} · 剩余约 {HumanDuration(remainSeconds)}";
    }

    /// <summary>
    /// 速度文本：1 MB/s 以上用 MB/s，以下用 KB/s（KB 档取整，个位数的 KB 抖动没有参考价值）。
    /// </summary>
    /// <param name="bytesPerSecond">平均下载速度，字节/秒</param>
    /// <returns>带单位的可读速度</returns>
    private static string HumanSpeed(double bytesPerSecond)
    {
        return bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / 1024 / 1024:0.0} MB/s"
            : $"{bytesPerSecond / 1024:0} KB/s";
    }

    /// <summary>
    /// 秒数转可读时长：&lt;60 秒按秒、&lt;60 分按「分+秒」、再往上按「小时+分」。
    /// </summary>
    /// <param name="seconds">剩余秒数，可能是 NaN / 无穷（速度异常时的除法结果）</param>
    /// <returns>如 "8 秒"、"12 分 05 秒"、"1 小时 05 分"；数值不可用时返回 "?"</returns>
    private static string HumanDuration(double seconds)
    {
        // 估算失败时显示一个 ? 而不是 "∞ 秒"，避免状态栏出现无意义字符
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "?";
        if (seconds < 60) return $"{Math.Ceiling(seconds):0} 秒";
        var minutes = (int)(seconds / 60);
        return minutes < 60
            ? $"{minutes} 分 {Math.Round(seconds % 60):00} 秒"
            : $"{minutes / 60} 小时 {minutes % 60:0} 分";
    }

    /// <summary>
    /// 点分十进制逐段比较，段数不等时缺的按 0 算（1.1 比 1.0.9 新）
    /// 从左往右找到第一个不相等的段就能定结论，后面的段再大也不参与比较。
    /// </summary>
    /// <param name="candidate">候选版本（清单里的新版本号）</param>
    /// <param name="current">当前版本（<see cref="CurrentVersion"/>）</param>
    /// <returns>candidate 严格比 current 新时为 true；相等或更旧为 false</returns>
    private static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            // 短的那边越界后补 0，等价于把 "1.1" 当成 "1.1.0" 比
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>
    /// 把版本串切成段号数组。分隔符同时认 . - +，遇到第一个非数字段就停（预发布后缀如 "-beta1" 直接忽略）。
    /// </summary>
    /// <param name="version">版本串，如 "1.0.3"、"1.0.3+abc123"、"1.0.3-beta"</param>
    /// <returns>至少含一个元素的段号数组；完全解析不出来时返回 [0]</returns>
    private static int[] Parse(string version)
    {
        var parts = version.Split('.', '-', '+');
        var list = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var n)) list.Add(n);
            else break;
        }
        return list.Count == 0 ? new[] { 0 } : list.ToArray();
    }
}
