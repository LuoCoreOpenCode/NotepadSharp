using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NotepadSharp.Services;

/// <summary>
/// 应用设置模型（持久化到 %APPDATA%\NotepadSharp\settings.json）
/// </summary>
public class AppSettings
{
    public string Theme { get; set; } = "Light";
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 10;
    public bool WordWrap { get; set; }
    public bool ShowWhitespace { get; set; }
    public bool ShowEol { get; set; }
    public bool ShowIndentGuide { get; set; } = true;
    public bool CaretLineHighlight { get; set; } = true;
    public bool AutoEolDetect { get; set; } = true;
    public bool ShowToolBar { get; set; } = true;

    /// <summary>超过该大小（MB）的文件在打开前提示</summary>
    public int BigFileThresholdMb { get; set; } = 100;

    public bool WatchExternalChanges { get; set; } = true;

    /// <summary>空格/制表符互转时只处理行首</summary>
    public bool TabLeadingOnly { get; set; }

    /// <summary>列编辑器上一次的参数，省得每次重新填</summary>
    public string ColumnEditorLastUsed { get; set; } = string.Empty;

    /// <summary>目录查找上一次的目录与文件类型</summary>
    public string DirFindRoot { get; set; } = string.Empty;
    public string DirFindFilter { get; set; } = "*.*";
    public bool DirFindSkipBinary { get; set; } = true;
    public bool DirFindSkipBigFile { get; set; } = true;
    public int DirFindMaxFileMb { get; set; } = 20;
    public string DirFindSkipDirNames { get; set; } = "debug:Debug:.vs:.git:.svn";

    /// <summary>查找/替换框的历史（最新在前，最多 25 条），与 ND2 查找窗的下拉一致</summary>
    public List<string> FindHistory { get; set; } = new();
    public List<string> ReplaceHistory { get; set; } = new();

    /// <summary>标记色 1..5 与是否轮色（ND2 Mark tab 的颜色选择）</summary>
    public int MarkColorSlot { get; set; } = 1;
    public bool MarkColorLoop { get; set; } = true;

    /// <summary>批量查找替换窗口上次的尺寸位置（ND2 用 saveGeometry 存 bfpos）</summary>
    public string BatchFindGeometry { get; set; } = string.Empty;

    /// <summary>查找结果面板：行数显示是否自动换行（ND2 结果区右键的 warp），0 表示没调过高度</summary>
    public bool SearchResultsWrap { get; set; }
    public double SearchResultsHeight { get; set; }

    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 700;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool Maximized { get; set; }

    /// <summary>工具栏图钉：整个程序固定置顶（重开后保持）</summary>
    public bool AlwaysOnTop { get; set; }

    public bool ShowFileTree { get; set; }
    public bool ShowMarkdown { get; set; }
    public bool ShowBookmarkBar { get; set; }

    public List<string> RecentFiles { get; set; } = new();
    public List<string> OpenFiles { get; set; } = new();
    public List<string> RecentDirs { get; set; } = new();
}

/// <summary>
/// 设置持久化服务
/// </summary>
public static class SettingsService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotepadSharp");

    private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    public static string SettingsFilePath => SettingsPath;

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // 忽略写入错误
        }
    }
}
