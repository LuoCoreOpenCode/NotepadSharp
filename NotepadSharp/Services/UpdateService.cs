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
/// </summary>
public static class UpdateService
{
    public const string GithubOwner = "LuoCoreOpenCode";

    public const string GithubRepo = "NotepadSharp";

    /// <summary>清单和安装包所在仓库的下载根地址</summary>
    private static string ReleaseDownloadRoot =>
        $"https://github.com/{GithubOwner}/{GithubRepo}/releases/latest/download";

    /// <summary>清单是个小 json，20 秒拿不到就当网络不通，别让用户干等</summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>自包含安装包有几十 MB，GitHub CDN 在国内实测只有 20~40 KB/s，给足 90 分钟</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(90) };

    private static int _checkRunning;

    private sealed record UpdateManifest(string Version, string File, string Sha256, string Notes);

    public static string CurrentVersion
    {
        get
        {
            var info = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) info = typeof(UpdateService).Assembly.GetName().Version?.ToString();
            // InformationalVersion 形如 "1.0.0+abc123"，构建元数据不参与比较
            var plus = info?.IndexOf('+') ?? -1;
            return plus > 0 ? info![..plus] : (info ?? "0.0.0");
        }
    }

    /// <summary>
    /// 检查并（经用户确认后）安装更新。showUpToDateDialog=false 用于启动时的静默检查：
    /// 没有新版本或网络不通时完全不打扰用户，只在确实有更新时才弹窗。
    /// </summary>
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
            Interlocked.Exchange(ref _checkRunning, 0);
        }
    }

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
            if (showUpToDateDialog)
                MessageBox.Show(owner, $"检查更新失败：{ex.Message}", "NotepadSharp 更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (manifest == null || !IsNewer(manifest.Version, CurrentVersion))
        {
            if (showUpToDateDialog)
                MessageBox.Show(owner, $"当前已是最新版本（{CurrentVersion}）。", "NotepadSharp 更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
            Process.Start(new ProcessStartInfo(setupPath, InstallerArguments)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"启动安装程序失败：{ex.Message}\n\n已下载到 {setupPath}，可手动运行。",
                "NotepadSharp 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// 静默装完要能把程序拉回来，所以不能只走管理员模式。
    /// 当前实例装在 %LOCALAPPDATA%\Programs 下说明上次是「仅当前用户」安装，
    /// 更新时得带 /CURRENTUSER，否则会弹 UAC 把静默流程卡死。
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

    private static bool BaseDirectoryStartsWith(string prefix)
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<UpdateManifest?> LoadManifestAsync()
    {
        // 留一个环境变量口子：内部试用时可以指向自己的镜像源或本地测试服务
        var overrideUrl = Environment.GetEnvironmentVariable("NOTEPADSHARP_UPDATE_MANIFEST");
        var url = string.IsNullOrWhiteSpace(overrideUrl)
            ? $"{ReleaseDownloadRoot}/latest.json"
            : overrideUrl;
        using var cts = new CancellationTokenSource(ManifestTimeout);
        var json = await Http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new UpdateManifest(
            ReadString(root, "version"),
            ReadString(root, "file"),
            ReadString(root, "sha256"),
            ReadString(root, "notes"));
    }

    private static string ReadString(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? string.Empty) : string.Empty;
        }
        return string.Empty;
    }

    private static async Task<string> DownloadAsync(UpdateManifest manifest, Action<string>? progress)
    {
        // file 写成完整 URL 时直接用它（指向别的下载源），否则按「最新 Release 的附件」拼
        var url = manifest.File.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? manifest.File
            : $"{ReleaseDownloadRoot}/{manifest.File}";
        var target = Path.Combine(Path.GetTempPath(), Path.GetFileName(manifest.File));
        if (File.Exists(target)) File.Delete(target);

        // ResponseHeadersRead：拿到响应头就能读 Content-Length，边下边报进度，不用等整包进内存
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;

        const int reportEveryPercent = 5;
        var lastReported = -reportEveryPercent;
        long done = 0;

        await using (var http = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await http.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    if (percent - lastReported >= reportEveryPercent || percent == 100)
                    {
                        lastReported = percent;
                        progress?.Invoke($"正在下载 {manifest.Version} 安装包 … {percent}%（{HumanSize(done)} / {HumanSize(total)}）");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            await using var check = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装包校验失败（sha256 不匹配），已放弃安装。");
        }

        return target;
    }

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
        return i == 0 ? $"{bytes} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    /// <summary>点分十进制逐段比较，段数不等时缺的按 0 算（1.1 比 1.0.9 新）</summary>
    private static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

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
