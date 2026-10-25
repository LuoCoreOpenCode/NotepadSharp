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

    private static bool _resolverInstalled;
    private static Dictionary<string, string>? _assemblyIndex;

    public static void EnsureResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    public static ResourceDictionary LoadDockTheme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "VsDockTheme.xaml");
        return (ResourceDictionary)XamlReader.Parse(File.ReadAllText(path));
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs e)
    {
        var simpleName = new AssemblyName(e.Name).Name;
        if (string.IsNullOrEmpty(simpleName)) return null;

        var index = _assemblyIndex ??= BuildIndex();
        if (!index.TryGetValue(simpleName, out var path)) return null;

        try
        {
            return Assembly.LoadFrom(path);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(AppContext.BaseDirectory, DockFolder);
        if (!Directory.Exists(root)) return map;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };
        foreach (var file in Directory.EnumerateFiles(root, "*.dll", options))
        {
            map.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }
        return map;
    }
}
