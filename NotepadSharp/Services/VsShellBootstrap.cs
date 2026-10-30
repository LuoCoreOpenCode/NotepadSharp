using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace NotepadSharp.Services;

/// <summary>
/// 加载 Visual Studio 的停靠内核 Microsoft.VisualStudio.Shell.ViewManager.dll 及其依赖：
/// 这些 dll 已内联在仓库 VsDlls\ 并随包发布到输出目录，运行时用 AssemblyResolve 从那里补齐，
/// 因此装到没有安装 VS 的机器上一样能跑。
/// </summary>
public static class VsShellBootstrap
{
    /// <summary>输出目录里存放停靠内核的子目录名</summary>
    private const string DockFolder = "VsDlls";

    /// <summary>解析器是否已挂上：EnsureResolver 会被多处调用，用这个标志保证只挂一次事件</summary>
    private static bool _resolverInstalled;

    /// <summary>
    /// 「程序集简单名 → dll 磁盘路径」索引，延迟构建（第一次真的解析不到时才扫盘）。
    /// 只做一次全量扫描，避免每次 AssemblyResolve 都去 EnumerateFiles，解析失败的路径也能直接命中字典。
    /// </summary>
    private static Dictionary<string, string>? _assemblyIndex;

    /// <summary>
    /// 挂上 AssemblyResolve 钩子。必须在创建任何停靠相关对象之前调用（App.OnStartup 里第一件事就是它），
    /// 否则运行时找 VsDlls\ 里的依赖时 CLR 已经判定加载失败，不会再回头问解析器。
    /// 幂等：重复调用只生效一次，不会把同一个处理器挂多份。
    /// </summary>
    public static void EnsureResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        // AssemblyResolve 只在常规探测（应用目录 / 依赖清单 / GAC）全失败后触发，
        // 返回 null 表示「我也没办法」，CLR 继续按原来的方式抛 FileNotFoundException
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    /// <summary>
    /// 读输出目录里的 VsDockTheme.xaml（停靠库控件的外观样式）并解析成 ResourceDictionary，
    /// 调用方（MainWindow.InitializeDocking）把它并入 Application.Current.Resources.MergedDictionaries，
    /// 让停靠标签、分组标题栏、浮动窗这些库自带控件能拿到 VS 风格的主题。
    /// 这里用 XamlReader.Parse 而不是 pack URI：文件是随包发布的普通磁盘文件，不是编译进 BAML 的资源，
    /// 且 XAML 内部引用 VsDlls 类型时正好由上面挂好的 AssemblyResolve 兜住。
    /// </summary>
    /// <returns>可直接合入应用级资源的字典；文件缺失或 XAML 有错会抛异常，由调用方决定如何提示</returns>
    public static ResourceDictionary LoadDockTheme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "VsDockTheme.xaml");
        return (ResourceDictionary)XamlReader.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// CLR 常规探测失败时的兜底解析：从输出目录的 VsDlls\ 里按简单名找 dll。
    /// </summary>
    /// <param name="sender">触发解析的 AppDomain，未使用</param>
    /// <param name="e">ResolveEventArgs，其 Name 是完整装配串（含版本、公钥标记、区域信息）</param>
    /// <returns>加载成功的程序集；索引里没有对应 dll 或加载失败时返回 null，让 CLR 走原本的失败路径</returns>
    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs e)
    {
        // 只取简单名：停靠内核的 dll 都是随包同版本一起发布的，按名字命中即可，
        // 严格比对版本号反而会因为 VS 内部程序集未递增版本而永远对不上
        var simpleName = new AssemblyName(e.Name).Name;
        if (string.IsNullOrEmpty(simpleName)) return null;

        // ??= 延迟建索引：程序不用停靠功能时一次盘都不扫
        var index = _assemblyIndex ??= BuildIndex();
        if (!index.TryGetValue(simpleName, out var path)) return null;

        try
        {
            // LoadFrom 会按路径登记到应用程序域，后续该 dll 的同目录依赖（含 satellite 资源目录）也能顺路找到
            return Assembly.LoadFrom(path);
        }
        catch
        {
            // dll 损坏 / 位数不匹配 / 被占用：宁可乐观地返回 null 让上层报「加载不到」，
            // 也不能让解析器把异常抛回 CLR 直接砸崩启动
            return null;
        }
    }

    /// <summary>
    /// 扫一遍 VsDlls\ 建「简单名 → 完整路径」索引。只在建索引时用，之后解析全走字典查找。
    /// </summary>
    /// <returns>大小写不敏感的索引；目录不存在时返回空字典（等价于「不接管任何解析」，程序照常起）</returns>
    private static Dictionary<string, string> BuildIndex()
    {
        // OrdinalIgnoreCase：Windows 文件名不区分大小写，装配串里的大小写写法和磁盘上不一致也要能命中
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(AppContext.BaseDirectory, DockFolder);
        if (!Directory.Exists(root)) return map;

        var options = new EnumerationOptions
        {
            // 只读文件 / 被占用 / 无权限的子目录直接跳过，别因为一个坏文件让索引建不起来
            IgnoreInaccessible = true,
            // 停靠内核解压后会带 runtimeconfig、资源等子目录，递归才能都收进来
            RecurseSubdirectories = true,
        };
        foreach (var file in Directory.EnumerateFiles(root, "*.dll", options))
        {
            // TryAdd：同名 dll 出现多次时保留第一个，不抛 ArgumentException
            map.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }
        return map;
    }
}
