using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NotepadSharp.Services;

/// <summary>
/// 应用设置模型（持久化到 %APPDATA%\NotepadSharp\settings.json）
/// 每个属性的默认值即「出厂种子」，尽量对齐 ND2 的注册表/ini 默认；反序列化失败时整体回落到本类的新实例。
/// </summary>
public class AppSettings
{
    /// <summary>主题名（"Light"/"Dark"），默认浅色</summary>
    public string Theme { get; set; } = "Light";
    /// <summary>正文字体族名，默认等宽 Consolas</summary>
    public string FontFamily { get; set; } = "Consolas";
    /// <summary>正文字号（磅），默认 10</summary>
    public double FontSize { get; set; } = 10;

    /// <summary>自动换行（ND2 键 warp，存的是 WrapCharacter/WrapNone，默认 0=不换行）</summary>
    public bool WordWrap { get; set; }

    /// <summary>
    /// 显示符号位域（ND2 键 blank：<c>ccnotepad.cpp:1059-1081</c>）：
    /// 1=显示空格 2=显示行尾 3=都显示 0=都不显示，出厂默认 0。
    /// 视图菜单的三项互锁走的就是这一位，不是两个独立开关。
    /// </summary>
    public int BlankShow { get; set; }   // 位域：bit0=空格、bit1=行尾，故 3 表示两者都显示

    /// <summary>缩进参考线（ND2 键 indent，工具栏按钮，默认 0=关）</summary>
    public bool ShowIndentGuide { get; set; }

    /// <summary>高亮当前光标所在行，默认开</summary>
    public bool CaretLineHighlight { get; set; } = true;
    /// <summary>打开时自动探测行尾符（CRLF/LF/CR），默认开</summary>
    public bool AutoEolDetect { get; set; } = true;

    /// <summary>显示工具栏（ND2 键 showbar，默认 1）</summary>
    public bool ShowToolBar { get; set; } = true;

    /// <summary>
    /// 高亮网页地址、双击打开（ND2 键 showweb，默认 0=关，菜单文案自己标了「不推荐开启」）。
    /// ND2 只在启动时读一次，切换时只改内存里那个静态位 + 落盘，不刷新已经画出来的热点。
    /// </summary>
    public bool ShowWebAddr { get; set; }

    /// <summary>Tab 宽度（ND2 键 tablens：对话框 spinbox 限 0-16，出厂种子 4）</summary>
    public int TabWidth { get; set; } = 4;

    /// <summary>
    /// 用空格替换 TAB 缩进（ND2 键 tabnouse：1=用空格、0=用 TAB）。
    /// ND2 出厂种子是 0，也就是默认仍然打真 TAB，这里照抄；界面文案沿用 ND2 的「使用空格替换tab」。
    /// </summary>
    public bool ReplaceTabWithSpace { get; set; }

    /// <summary>超过该大小（MB）的文件在打开前提示</summary>
    public int BigFileThresholdMb { get; set; } = 100;

    /// <summary>监视外部修改、发现冲突时提示是否重载，默认开</summary>
    public bool WatchExternalChanges { get; set; } = true;

    /// <summary>列编辑器上一次的参数，省得每次重新填</summary>
    public string ColumnEditorLastUsed { get; set; } = string.Empty;

    /// <summary>目录查找上一次的目录与文件类型</summary>
    public string DirFindRoot { get; set; } = string.Empty;         // 上次搜索的根目录
    public string DirFindFilter { get; set; } = "*.*";              // 文件类型过滤（通配符）
    public bool DirFindSkipBinary { get; set; } = true;             // 跳过已知二进制文件
    public bool DirFindSkipBigFile { get; set; } = true;            // 跳过超过阈值的文件
    public int DirFindMaxFileMb { get; set; } = 20;                 // 配合 SkipBigFile 的单文件大小上限（MB）
    public string DirFindSkipDirNames { get; set; } = "debug:Debug:.vs:.git:.svn";   // 冒号分隔的要跳过的目录名（含大小写两种写法）

    /// <summary>查找/替换框的历史（最新在前，最多 25 条），与 ND2 查找窗的下拉一致</summary>
    public List<string> FindHistory { get; set; } = new();      // 查找词历史
    public List<string> ReplaceHistory { get; set; } = new();   // 替换词历史

    /// <summary>标记色 1..5 与是否轮色（ND2 Mark tab 的颜色选择）</summary>
    public int MarkColorSlot { get; set; } = 1;      // 当前标记色槽位（1..5）
    public bool MarkColorLoop { get; set; } = true;  // 是否每标记一次自动轮换到下一色槽

    /// <summary>批量查找替换窗口上次的尺寸位置（ND2 用 saveGeometry 存 bfpos）</summary>
    public string BatchFindGeometry { get; set; } = string.Empty;

    /// <summary>查找结果面板：行数显示是否自动换行（ND2 结果区右键的 warp），0 表示没调过高度</summary>
    public bool SearchResultsWrap { get; set; }          // 结果行是否换行显示
    public double SearchResultsHeight { get; set; }      // 结果面板上次高度，0=未调整过

    /// <summary>主窗口宽度（DIP），默认 1100</summary>
    public double WindowWidth { get; set; } = 1100;
    /// <summary>主窗口高度（DIP），默认 700</summary>
    public double WindowHeight { get; set; } = 700;
    /// <summary>主窗口左坐标；可空，null 表示从未记录（首次启动居中显示）</summary>
    public double? WindowLeft { get; set; }
    /// <summary>主窗口顶坐标；可空，null 表示从未记录</summary>
    public double? WindowTop { get; set; }
    /// <summary>主窗口是否最大化，默认否</summary>
    public bool Maximized { get; set; }

    /// <summary>工具栏图钉：整个程序固定置顶（重开后保持）</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>是否显示左侧文档树面板，默认关</summary>
    public bool ShowFileTree { get; set; }
    /// <summary>是否显示 Markdown 预览面板，默认关</summary>
    public bool ShowMarkdown { get; set; }
    /// <summary>是否显示书签栏，默认关</summary>
    public bool ShowBookmarkBar { get; set; }

    /// <summary>最近文件列表（供「文件-最近打开」菜单）</summary>
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>会话中已打开的文件（启动时自动恢复，无需弹窗确认）</summary>
    public List<string> OpenFiles { get; set; } = new();
    /// <summary>最近目录列表（供目录相关下拉）</summary>
    public List<string> RecentDirs { get; set; } = new();
}

/// <summary>
/// 设置持久化服务：负责把 <see cref="AppSettings"/> 以 JSON 读写到磁盘，并暴露进程内的当前实例 <see cref="Current"/>。
/// </summary>
public static class SettingsService
{
    /// <summary>设置文件所在目录，由 <see cref="ResolveDir"/> 决定（支持环境变量覆盖以便测试隔离）</summary>
    private static readonly string Dir = ResolveDir();

    /// <summary>settings.json 的完整路径（Dir + 文件名）</summary>
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");

    // 冒烟测试用临时设置目录，避免碰到用户真实的 settings.json
    /// <summary>
    /// 解析设置目录：若设置了环境变量 NOTEPADSHARP_SETTINGS_DIR，就用它做隔离目录（测试专用）；
    /// 否则回落到用户 %APPDATA%\NotepadSharp。返回的目录此时可能尚不存在，Save 时才 CreateDirectory。
    /// </summary>
    /// <returns>设置文件所在目录的绝对路径</returns>
    private static string ResolveDir()
    {
        // 读取测试隔离用的覆盖目录；未设置时走真实 APPDATA 路径
        var overrideDir = Environment.GetEnvironmentVariable("NOTEPADSHARP_SETTINGS_DIR");
        return string.IsNullOrEmpty(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotepadSharp")
            : overrideDir;
    }

    /// <summary>JSON 序列化/反序列化选项：WriteIndented=true 便于人工查看与 diff</summary>
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>进程内的当前设置实例；未加载或加载失败时为新构造的出厂默认值</summary>
    public static AppSettings Current { get; private set; } = new();

    /// <summary>对外只读暴露当前设置文件的完整路径（便于诊断/测试断言）</summary>
    public static string SettingsFilePath => SettingsPath;

    /// <summary>
    /// 从磁盘载入设置到 <see cref="Current"/>。
    /// 文件不存在则保留出厂默认；读取或反序列化任何异常都吞掉并回落到全新 <see cref="AppSettings"/>，
    /// 保证坏文件/缺文件都不会让程序启动失败。
    /// </summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                // 读全文再反序列化；?? 兜底防御 JSON 字面量 "null" 使 Deserialize 返回 null 的情况
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            // 文件损坏、权限不足等一律忽略，用出厂默认继续运行
            Current = new AppSettings();
        }
    }

    /// <summary>
    /// 把 <see cref="Current"/> 序列化成缩进 JSON 写入 SettingsPath。
    /// 先 CreateDirectory 兜底目录不存在（或环境变量指向的临时目录）；任何写盘异常都被吞掉以保证不崩。
    /// </summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);   // 目录可能尚未创建，尤其 NOTEPADSHARP_SETTINGS_DIR 指向新临时目录时
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // 忽略写入错误
        }
    }
}
