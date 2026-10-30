using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI.Shell;
using Microsoft.VisualStudio.PlatformUI.Shell.Controls;
using Microsoft.Win32;
using NotepadSharp.Controls;
using NotepadSharp.Models;
using NotepadSharp.Services;
using NotepadSharp.Views;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Point = System.Windows.Point;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using View = Microsoft.VisualStudio.PlatformUI.Shell.View;
using Orientation = System.Windows.Controls.Orientation;

namespace NotepadSharp;

/// <summary>
/// 主窗口：整个应用的宿主。承载 VS 停靠内核（文档标签组/浮动窗格）、工具栏、
/// 查找替换/目录查找/搜索结果等内嵌面板、状态栏，以及全部菜单命令。
/// 行为逐条对照 ND2（Qt/C++ 参考实现 ccnotepad.cpp），代码中的 文件名:行号 注释即参考实现出处。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>已打开的全部文档模型，与标签页一一对应；「保存全部/关闭全部」等批量操作按此列表遍历</summary>
    private List<Document> _documents { get; } = new();
    /// <summary>启动时要恢复的上次会话文件（构造时收集，窗口 Loaded 后逐个静默打开，见构造函数注释）</summary>
    private readonly List<string> _restoreFiles = new();
    /// <summary>文档模型 → 标签页布局项（DocumentLayoutItem）的映射；标签即「文档」在停靠树里的化身</summary>
    private Dictionary<Document, DocumentLayoutItem> _documentItems = new();
    /// <summary>标签页布局项 → 编辑器控件的映射；一个标签固定一个 EditorControl</summary>
    private Dictionary<DocumentLayoutItem, EditorControl> _itemEditors = new();
    /// <summary>标签页布局项 → VS 停靠 View 的映射；AttachView 建立、DetachView 解除</summary>
    private Dictionary<DocumentLayoutItem, View> _itemViews = new();
    /// <summary>反向映射：VS 停靠 View → 标签页布局项；停靠事件里只能拿到 View，用它找回我们的模型</summary>
    private Dictionary<View, DocumentLayoutItem> _viewItems = new();
    /// <summary>VS 停靠库的窗口布局档案（所有 View/分组的树），持久化与查找布局节点都靠它</summary>
    private WindowProfile? _windowProfile;
    /// <summary>当前文档（命令的默认作用对象）。注意：SaveAll/CloseTab 等会临时改指别的文档</summary>
    private Document? _currentDocument;
    /// <summary>防重入标志：正走「View 隐藏 → 关闭文档」流程时，忽略由此再次触发的 IsVisibleChanged</summary>
    private bool _closingFromVisibility;
    /// <summary>「新建 N」标签的自增序号，仅用于给未命名文档排编号</summary>
    private int _newFileCounter;
    /// <summary>窗口真正可用（Loaded 且初始文件都打开完）之后才允许往状态栏报启动类消息，避免早于界面就绪</summary>
    private bool _readyForStatusMessages;

    // 查找替换状态：面板开着时以面板为准，关掉后 F3/F4 重复这一组条件（ND2 的行为）
    /// <summary>最近一次生效的查找条件（面板每次动作时由 CommitFindState 同步过来）</summary>
    private SearchCondition _lastFind = SearchCondition.Empty;
    /// <summary>最近一次生效的替换文本</summary>
    private string _lastReplace = string.Empty;
    /// <summary>是否循环查找（查到末尾后从头找），对应面板上的「循环查找」勾选</summary>
    private bool _wrapAround = true;
    /// <summary>最近一次查找的方向；F4 反向重复时会临时覆盖，不写回这里</summary>
    private bool _backwardSearch;

    // 命令
    /// <summary>新建标签页（Ctrl+N）</summary>
    public ICommand NewFileCommand { get; private set; } = null!;
    /// <summary>弹出打开文件对话框，可多选（Ctrl+O）</summary>
    public ICommand OpenFileCommand { get; private set; } = null!;
    /// <summary>保存当前文档；无路径时转为另存为（Ctrl+S）</summary>
    public ICommand SaveFileCommand { get; private set; } = null!;
    /// <summary>另存为（Ctrl+Shift+S）</summary>
    public ICommand SaveAsCommand { get; private set; } = null!;
    /// <summary>撤销一步编辑</summary>
    public ICommand UndoCommand { get; private set; } = null!;
    /// <summary>重做一步编辑</summary>
    public ICommand RedoCommand { get; private set; } = null!;
    /// <summary>剪贴板安全剪切（避开 Scintilla 剪切与 WPF 焦点抢答的竞态）</summary>
    public ICommand CutCommand { get; private set; } = null!;
    /// <summary>剪贴板安全复制</summary>
    public ICommand CopyCommand { get; private set; } = null!;
    /// <summary>剪贴板安全粘贴</summary>
    public ICommand PasteCommand { get; private set; } = null!;
    /// <summary>全选当前文档（Ctrl+A）</summary>
    public ICommand SelectAllCommand { get; private set; } = null!;
    /// <summary>行注释开关（对选区/当前行加或去掉行注释符）</summary>
    public ICommand LineCommentCommand { get; private set; } = null!;
    /// <summary>为选区包裹块注释</summary>
    public ICommand AddBlockCommentCommand { get; private set; } = null!;
    /// <summary>去掉选区的块注释</summary>
    public ICommand DelBlockCommentCommand { get; private set; } = null!;
    /// <summary>选区转大写</summary>
    public ICommand UpperCaseCommand { get; private set; } = null!;
    /// <summary>选区转小写</summary>
    public ICommand LowerCaseCommand { get; private set; } = null!;
    /// <summary>打开查找替换面板（Ctrl+F）</summary>
    public ICommand FindCommand { get; private set; } = null!;
    /// <summary>查找下一个（F3）</summary>
    public ICommand FindNextCommand { get; private set; } = null!;
    /// <summary>查找上一个（Shift+F3 / F4）</summary>
    public ICommand FindPreviousCommand { get; private set; } = null!;
    /// <summary>跳转到指定行（Ctrl+G）</summary>
    public ICommand GoToLineCommand { get; private set; } = null!;
    /// <summary>在当前行切换书签（Ctrl+F2）</summary>
    public ICommand ToggleBookmarkCommand { get; private set; } = null!;
    /// <summary>跳到下一个书签（F2）</summary>
    public ICommand NextBookmarkCommand { get; private set; } = null!;
    /// <summary>跳到上一个书签（Shift+F2）</summary>
    public ICommand PreviousBookmarkCommand { get; private set; } = null!;
    /// <summary>清除当前文档的全部书签</summary>
    public ICommand ClearBookmarksCommand { get; private set; } = null!;
    /// <summary>折叠全部代码块</summary>
    public ICommand CollapseAllCommand { get; private set; } = null!;
    /// <summary>展开全部代码块</summary>
    public ICommand ExpandAllCommand { get; private set; } = null!;
    /// <summary>亮/暗主题切换（Ctrl+T）</summary>
    public ICommand ToggleThemeCommand { get; private set; } = null!;
    /// <summary>显示/隐藏左侧文件树面板</summary>
    public ICommand ToggleFileTreeCommand { get; private set; } = null!;
    /// <summary>显示/隐藏 Markdown 预览面板</summary>
    public ICommand ToggleMarkdownCommand { get; private set; } = null!;
    /// <summary>显示/隐藏底部书签列表面板</summary>
    public ICommand ToggleBookmarkBarCommand { get; private set; } = null!;
    /// <summary>关闭主窗口退出程序</summary>
    public ICommand ExitCommand { get; private set; } = null!;
    /// <summary>显示「关于」对话框</summary>
    public ICommand AboutCommand { get; private set; } = null!;
    /// <summary>自动换行开关（对所有已打开文档生效并落盘）</summary>
    public ICommand ToggleWordWrapCommand { get; private set; } = null!;
    /// <summary>显示空格/TAB 开关（空白字符位域 bit0）</summary>
    public ICommand ToggleSpacesCommand { get; private set; } = null!;
    /// <summary>显示行尾符开关（空白字符位域 bit1）</summary>
    public ICommand ToggleLineEndCommand { get; private set; } = null!;
    /// <summary>显示所有空白字符开关（位域 3 = 全开，否则全关）</summary>
    public ICommand ToggleAllWhiteCommand { get; private set; } = null!;
    /// <summary>显示/隐藏顶部工具栏</summary>
    public ICommand ToggleToolBarCommand { get; private set; } = null!;
    /// <summary>显示/隐藏 URL 热点（进程级开关，不刷新已开编辑器，同 ND2）</summary>
    public ICommand ToggleWebAddrCommand { get; private set; } = null!;
    /// <summary>放大当前编辑器字体（Ctrl+滚轮同源）</summary>
    public ICommand ZoomInCommand { get; private set; } = null!;
    /// <summary>缩小当前编辑器字体</summary>
    public ICommand ZoomOutCommand { get; private set; } = null!;
    /// <summary>缩放回到 100%</summary>
    public ICommand ZoomResetCommand { get; private set; } = null!;
    /// <summary>依次保存所有已修改/未命名的文档</summary>
    public ICommand SaveAllCommand { get; private set; } = null!;
    /// <summary>关闭当前标签（Ctrl+W，带保存确认与锁定检查）</summary>
    public ICommand CloseFileCommand { get; private set; } = null!;
    /// <summary>关闭全部标签</summary>
    public ICommand CloseAllCommand { get; private set; } = null!;
    /// <summary>除当前标签外全部关闭</summary>
    public ICommand CloseOtherCommand { get; private set; } = null!;
    /// <summary>关闭当前标签以左的所有标签</summary>
    public ICommand CloseLeftCommand { get; private set; } = null!;
    /// <summary>关闭当前标签以右的所有标签</summary>
    public ICommand CloseRightCommand { get; private set; } = null!;
    /// <summary>丢弃改动、按文档编码从磁盘重读</summary>
    public ICommand ReloadFileCommand { get; private set; } = null!;
    /// <summary>在资源管理器中打开当前文件所在目录</summary>
    public ICommand OpenFolderCommand { get; private set; } = null!;
    /// <summary>在光标处插入 yyyy-MM-dd HH:mm:ss</summary>
    public ICommand InsertDateCommand { get; private set; } = null!;
    /// <summary>弹出字数统计对话框</summary>
    public ICommand StatsCommand { get; private set; } = null!;
    /// <summary>在当前文件目录开一个 cmd.exe</summary>
    public ICommand OpenCmdCommand { get; private set; } = null!;
    /// <summary>给当前文档每一行加书签</summary>
    public ICommand MarkAllCommand { get; private set; } = null!;
    /// <summary>格式化 JSON（选区或全文，Ctrl+Shift+F）</summary>
    public ICommand FormatJsonCommand { get; private set; } = null!;
    /// <summary>压缩 JSON（选区或全文，Ctrl+Shift+M）</summary>
    public ICommand MinifyJsonCommand { get; private set; } = null!;
    /// <summary>把当前文档转成 HTML 并在新标签展示</summary>
    public ICommand ConvertToHtmlCommand { get; private set; } = null!;
    /// <summary>格式化 XML（选区或全文）</summary>
    public ICommand FormatXmlCommand { get; private set; } = null!;
    /// <summary>压缩 XML（选区或全文）</summary>
    public ICommand MinifyXmlCommand { get; private set; } = null!;
    /// <summary>缩进参考线开关（跳过 hex 页，同 ND2）</summary>
    public ICommand ToggleIndentGuideCommand { get; private set; } = null!;
    /// <summary>强制切亮色主题</summary>
    public ICommand LightThemeCommand { get; private set; } = null!;
    /// <summary>强制切暗色主题</summary>
    public ICommand DarkThemeCommand { get; private set; } = null!;
    /// <summary>在资源管理器中打开设置文件所在目录</summary>
    public ICommand OpenSettingsFolderCommand { get; private set; } = null!;
    /// <summary>清除当前文档的查找高亮</summary>
    public ICommand ClearHighlightCommand { get; private set; } = null!;
    /// <summary>锁定/解锁当前标签：防误关、防切编码（Ctrl+Alt+T）</summary>
    public ICommand ToggleTabLockCommand { get; private set; } = null!;
    /// <summary>窗口固定置顶开关（Ctrl+Alt+P）</summary>
    public ICommand ToggleTopmostCommand { get; private set; } = null!;
    /// <summary>位置回退：跳回上一个编辑/跳转位置（ND2 与 hex 翻页共用按钮）</summary>
    public ICommand PrePosCommand { get; private set; } = null!;
    /// <summary>位置前进：跳回位置回退之前的位置</summary>
    public ICommand NextPosCommand { get; private set; } = null!;
    /// <summary>剪切所有被标记（书签）行</summary>
    public ICommand CutMarkedLinesCommand { get; private set; } = null!;
    /// <summary>复制所有被标记（书签）行</summary>
    public ICommand CopyMarkedLinesCommand { get; private set; } = null!;
    /// <summary>把剪贴板内容粘贴到每个被标记行的行首</summary>
    public ICommand PasteMarkedLinesCommand { get; private set; } = null!;
    /// <summary>删除所有被标记行</summary>
    public ICommand DeleteMarkedLinesCommand { get; private set; } = null!;
    /// <summary>删除所有未被标记的行（即只留被标记行）</summary>
    public ICommand DeleteUnmarkedLinesCommand { get; private set; } = null!;
    /// <summary>反选书签：原有书签去掉、没有的补上</summary>
    public ICommand InvertMarksCommand { get; private set; } = null!;

    /// <summary>
    /// 主窗口构造：顺序固定为「读设置 → XAML 初始化 → 停靠内核 → 拖放 → 命令/动态菜单 →
    /// 回填设置 → 收集待恢复文件」。注意动态菜单必须在 InitializeComponent 之后构建（要 FindResource 拿模板）。
    /// </summary>
    public MainWindow()
    {
        // 先读用户设置，后面的界面构建（主题/面板可见性/最近文件）全部以它为准
        SettingsService.Load();

        InitializeComponent();
        InitializeDocking();
        EnableFileDrop(this);
        // 命令都挂在 this 上，XAML 里 Binding XxxCommand 依赖这一步
        DataContext = this;
        InitializeCommands();
        // 这几个菜单条目多且带快捷键回显，由代码统一生成（XAML 里只留占位 MenuItem）
        BuildEncodingMenu();
        BuildFormatMenu();
        BuildLanguageMenu();
        BuildMarkColorMenu();
        BuildFileTreeContextMenu();
        ApplySettingsToWindow();

        // 命令行文件留到窗口显示后再开：OpenFile 可能弹「打开为文本或二进制」，
        // 窗口还没 Show 时既不能给它设 Owner，主窗口也要等弹窗关掉才出现。
        // 会话恢复一并放这里，等窗口显示后再补标签，且不弹任何询问框。
        _restoreFiles.AddRange(SettingsService.Current.OpenFiles.Where(File.Exists));
        if (string.IsNullOrEmpty(App.PendingOpenFile) && _restoreFiles.Count == 0)
            NewFile();

        // 窗口加载完成后再次应用主题，确保整个可视树刷新
        Loaded += (_, _) =>
        {
            // 恢复上次会话不打扰用户：不弹二进制三选一，也不弹大文件确认
            foreach (var path in _restoreFiles)
                OpenFile(path, reloadEncoding: null, quiet: true);
            _restoreFiles.Clear();

            if (!string.IsNullOrEmpty(App.PendingOpenFile))
            {
                var pending = App.PendingOpenFile;
                App.PendingOpenFile = null;
                OpenFile(pending);
            }
            ApplyThemeToUI();
            _readyForStatusMessages = true;
            ScheduleStartupUpdateCheck();
        };
    }

    // ==================== VS 停靠内核 ====================

    /// <summary>把 VS 的 ViewManager 挂到 DockHost：主题 + 布局配置 + 文档标签组</summary>
    private void InitializeDocking()
    {
        // 单例 ViewManager 接管 XAML 里的 DockHost 控件，之后所有 View 的增删都通过它驱动
        var vm = ViewManager.Instance;
        vm.Initialize(DockHost);

        // 停靠库自带的控件主题（标签/分组标题栏的样式）并入应用级资源
        Application.Current.Resources.MergedDictionaries.Add(VsShellBootstrap.LoadDockTheme());

        // 建一份布局档案；新建的 View 都挂在这棵树上。把档案里已有元素全部置可见，
        // 否则重启恢复时隐藏的工具窗口会占位不显示
        _windowProfile = WindowProfile.Create("Default");
        foreach (var element in _windowProfile.FindAll<ViewElement>())
            element.IsVisible = true;
        vm.WindowProfile = _windowProfile;
        // 活动 View 变化 = 「当前标签/当前窗格」变了，状态栏与文件树跟着刷新
        vm.ActiveViewChanged += OnVsActiveViewChanged;

        // 浮动窗标题栏的 ✕ 走命令路由：统一做保存确认 + 锁定检查（见 OnCloseFloatingViewCommand）
        CommandManager.RegisterClassCommandBinding(typeof(FloatingWindow),
            new CommandBinding(VsDockCommands.CloseFloatingView,
                OnCloseFloatingViewCommand, OnCanCloseFloatingViewCommand));

        // 标签右键菜单：停靠库只把事件冒泡出来，菜单本体要自己搭
        DockHost.AddHandler(DragUndockHeader.DragHeaderContextMenuEvent,
            new EventHandler<DragUndockHeaderContextMenuEventArgs>(OnTabHeaderContextMenu));

        // 浮动窗弹出后统一「整形」：尺寸/位置修正、置顶继承、抬层与拖放都要补挂
        FloatingWindowManager.FloatingWindowShown += (_, e) =>
        {
            // 主窗口的 Topmost 先取快照：BeginInvoke 排队期间用户可能又切了置顶，但按弹出那一刻算更符合直觉
            bool keepOnTop = Topmost;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NormalizeFloatingWindow(e.Window);
                // 浮动窗格是独立顶层窗口，不跟着主窗口的 Topmost 走，得单独补
                e.Window.Topmost = keepOnTop;
                HookRaiseOnActivate(e.Window);
                // 浮动窗格也要能接收拖进来的文件
                EnableFileDrop(e.Window);
            }), DispatcherPriority.Background);
        };

        // 拖拽中一旦把焦点转给 WinForms 编辑器，WPF 窗口会短暂失活，
        // 停靠反馈窗（拖拽预览）跟着关闭再打开，看起来就是一闪一闪；所以拖拽期间推迟这些副作用。
        // 按下标签头即认为「可能开始拖」；真正的解除靠下面两个松手/完成事件（任一路径到达都行）
        DockHost.PreviewMouseLeftButtonDown += (_, _) => _headerDragActive = true;
        PreviewMouseLeftButtonUp += (_, _) => FlushDeferredActivation();
        DockHost.AddHandler(DragUndockHeader.DragCompletedAbsoluteEvent,
            new EventHandler<DragAbsoluteCompletedEventArgs>((_, e) => FlushDeferredActivation()));

        Activated += (_, _) => RaiseAboveEverything();
        EditorControl.EditorMouseDown += RaiseAboveEverything;
        // 工具栏/标签条/文件树这些 WPF 区域的点击既不产生 WM_MOUSEACTIVATE（窗口已是前台），
        // 也不进 Scintilla 的 WinForms 事件，只能在窗口级输入事件里补
        PreviewMouseDown += (_, _) => RaiseAboveEverything();
    }

    /// <summary>标签头拖拽进行中的标志：true 时推迟「抢焦点/刷文件树/抬层」这类会打断停靠反馈的副作用</summary>
    private bool _headerDragActive;
    /// <summary>拖拽期间被推迟的「让编辑器抢焦点」，松手后由 FlushDeferredActivation 补做</summary>
    private EditorControl? _pendingFocusEditor;
    /// <summary>拖拽期间被推迟的「文件树跟随当前文档切根目录」，松手后补做</summary>
    private bool _pendingFileTreeRefresh;

    /// <summary>拖拽结束：把拖拽期间攒下的焦点/文件树副作用一次性补执行，然后清标志</summary>
    private void FlushDeferredActivation()
    {
        if (!_headerDragActive) return;
        _headerDragActive = false;
        // 文件树切根涉及清空/重建 TreeView，放拖拽里做会卡反馈窗，所以只在松手后补
        if (_pendingFileTreeRefresh)
        {
            _pendingFileTreeRefresh = false;
            RefreshFileTreeForCurrentFile();
        }
        // 焦点交回编辑器：WinForms 控件 Focus 会触发窗口失活/激活链，必须等拖拽彻底结束
        if (_pendingFocusEditor != null)
        {
            var editor = _pendingFocusEditor;
            _pendingFocusEditor = null;
            editor.FocusEditor();
        }
    }

    /// <summary>SetWindowPos 的「只改 Z 序不动位置/尺寸/激活」标志组合：三个标志按位或后传入</summary>
    private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    /// <summary>置顶带最顶句柄：先挂 TOPMOST 能把霸屏窗口挤下去</summary>
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    /// <summary>普通层最顶句柄：随后补一发 NOTOPMOST，窗口不会真的留在置顶带（图钉关闭时也用这一对）</summary>
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    /// <summary>虚拟键码：鼠标左键，配合 GetAsyncKeyState 探测真实按键状态</summary>
    private const int VK_LBUTTON = 0x01;

    /// <summary>Win32 调整窗口位置/Z 序；这里只借它改 Z 序，坐标参数在 SWP_NOSIZE|NOMOVE 下被忽略</summary>
    /// <param name="hWnd">目标窗口句柄</param>
    /// <param name="hWndInsertAfter">插入位置参考句柄（HWND_TOPMOST / HWND_NOTOPMOST 等哨兵值）</param>
    /// <param name="X">新左边界；带 SWP_NOMOVE 时忽略</param>
    /// <param name="Y">新上边界；带 SWP_NOMOVE 时忽略</param>
    /// <param name="cx">新宽度；带 SWP_NOSIZE 时忽略</param>
    /// <param name="cy">新高度；带 SWP_NOSIZE 时忽略</param>
    /// <param name="uFlags">SWP_ 位标志组合（本文件用 NOSIZE|NOMOVE|NOACTIVATE）</param>
    /// <returns>成功改位置为 true；本处只调用不判定，失败等价于「抬层没生效」</returns>
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Win32 查询按键全局异步状态，返回值最高位为 1 表示当前正被按下</summary>
    /// <param name="vKey">虚拟键码（本文件传 VK_LBUTTON）</param>
    /// <returns>按键状态字；bit15=1 表示此刻正被按下</returns>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // 外部文件拖放：Scintilla 自己在子窗口上注册了 IDropTarget（管文本拖拽），
    // WinForms 的 AllowDrop 抢不过它；而 OLE 在目标拒绝文件拖放时会退回传统的 WM_DROPFILES，
    // 沿父链找注册过 DragAcceptFiles 的窗口投递 —— 实测在编辑器区域落下也能收到，且不干扰文本拖拽。
    /// <summary>Win32 声明「本窗口愿意接收旧式文件拖放（WM_DROPFILES）」</summary>
    /// <param name="hWnd">要注册/注销拖放目标的窗口句柄</param>
    /// <param name="accept">true 接收，false 取消接收</param>
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    /// <summary>Win32 读取拖放句柄里的文件：iFile=0xFFFFFFFF 返回文件总数；传 buffer 则把第 iFile 个路径写进去并返回其长度</summary>
    /// <param name="hDrop">WM_DROPFILES 带来的 HDROP 句柄</param>
    /// <param name="iFile">要取的第 i 个文件；0xFFFFFFFF 表示「只问总数」</param>
    /// <param name="buffer">接收路径的缓冲区；传 null 表示只问该路径的长度</param>
    /// <param name="bufferCount">缓冲区容量（字符数）</param>
    /// <returns>问总数时返回文件个数；否则返回该路径的字符长度（0 表示读取失败）</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? buffer, uint bufferCount);

    /// <summary>Win32 释放拖放句柄（读完必须调用，否则系统泄漏内存）</summary>
    /// <param name="hDrop">此前由 WM_DROPFILES 提供、已读完的 HDROP 句柄</param>
    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>窗口消息：旧式文件拖放落下（wParam 是 HDROP 句柄）</summary>
    private const int WM_DROPFILES = 0x0233;
    /// <summary>已挂过拖放钩子的顶层窗口集合，防止同一窗口重复 AddHook</summary>
    private readonly HashSet<Window> _dropHookedWindows = new();

    /// <summary>让某个顶层窗口（主窗口 / VS 停靠的浮动窗）能接收外部文件拖放</summary>
    /// <param name="window">要挂钩的顶层窗口</param>
    private void EnableFileDrop(Window window)
    {
        // Add 失败说明已挂过；窗口关闭时把钩子记录清掉（HwndSource 随窗口销毁，无需显式 RemoveHook）
        if (!_dropHookedWindows.Add(window)) return;
        window.Closed += (_, _) => _dropHookedWindows.Remove(window);

        // 局部函数：真正的挂钩动作 —— 拿到 HWND 后加消息钩子并声明接收文件拖放
        /// <summary>取当前窗口的 HWND，挂 WM_DROPFILES 钩子并向 Shell 声明本窗口接收文件拖放</summary>
        void Hook()
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            source.AddHook(DropWndProc);
            try { DragAcceptFiles(source.Handle, true); } catch { /* 注册失败只影响拖放，不影响编辑 */ }
        }

        // 主窗口在构造时订阅（句柄还没建）；浮动窗弹出时句柄可能已经建好了，那就立刻挂钩
        if (PresentationSource.FromVisual(window) is HwndSource) Hook();
        else window.SourceInitialized += (_, _) => Hook();
    }

    /// <summary>WM_DROPFILES 消息钩子：读出文件路径列表后转交打开逻辑</summary>
    /// <param name="hwnd">收到消息的窗口句柄（主窗或浮动窗）</param>
    /// <param name="msg">消息编号，仅 WM_DROPFILES 才处理</param>
    /// <param name="wParam">HDROP 句柄，拖放数据的内存块指针</param>
    /// <param name="lParam">未使用</param>
    /// <param name="handled">是否已消费本消息；本钩子不置位</param>
    /// <returns>恒返回 IntPtr.Zero —— 让其它钩子/默认过程照常处理</returns>
    private IntPtr DropWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES) return IntPtr.Zero;
        var paths = ReadDroppedFiles(wParam);
        // 放到 BeginInvoke 再开：钩子在消息泵里同步执行，直接弹窗/建标签会重入消息循环
        if (paths.Count > 0) Dispatcher.BeginInvoke(new Action(() => OpenDroppedPaths(paths)));
        return IntPtr.Zero;
    }

    /// <summary>从 HDROP 句柄里逐个取出文件全路径；无论成功与否都要 DragFinish 归还句柄</summary>
    /// <param name="hDrop">WM_DROPFILES 的 wParam，即 HDROP 句柄；为 Zero 时直接返回空表</param>
    /// <returns>本次拖放携带的文件路径列表（可能含目录，由调用方甄别）</returns>
    private static List<string> ReadDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        if (hDrop == IntPtr.Zero) return paths;
        try
        {
            // 第一遍：0xFFFFFFFF 这个魔法参数表示「问总数」
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                // 第二遍：buffer 传 null 得到第 i 个路径的长度，再按长度分配 StringBuilder 取值
                uint length = DragQueryFileW(hDrop, i, null, 0);
                if (length == 0) continue;
                var sb = new StringBuilder((int)length + 1);
                if (DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity) == 0) continue;
                paths.Add(sb.ToString());
            }
        }
        finally { DragFinish(hDrop); }
        return paths;
    }

    /// <summary>拖进来的文件按普通「打开」处理；目录则切给左侧文件树</summary>
    /// <param name="paths">从 HDROP 读出的路径列表（文件或目录混合）</param>
    private void OpenDroppedPaths(List<string> paths)
    {
        int opened = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                OpenFile(path);   // OpenFile 自己会刷新文件树与最近文件
                opened++;
            }
            else if (Directory.Exists(path))
            {
                FileTreePanel.Visibility = Visibility.Visible;
                SetFileTreeRoot(path);
            }
        }
        if (opened > 0) StatusBarMessage($"已拖入打开 {opened} 个文件");
    }

    /// <summary>RaiseAboveEverything 的节流时间戳（Environment.TickCount64），避免一次点击触发多路抬层</summary>
    private long _lastRaiseTick;
    /// <summary>已挂过「激活即抬层」钩子的浮动窗集合，防重复订阅</summary>
    private readonly HashSet<Window> _raiseHookedWindows = new();

    /// <summary>
    /// 把窗口抬到桌面上所有窗口之上。有些窗口（浏览器/Electron 类）会不断把自己按回最前，
    /// 普通 SetWindowPos(HWND_TOP) 甚至系统激活都压不住它；先挂 TOPMOST 再退回 NOTOPMOST
    /// 这一对调用能把它挤下去，而且窗口不会真的留在置顶带里（图钉关闭时也一样）。
    /// </summary>
    /// <param name="window">要抬层的窗口</param>
    private static void RaiseWindowAboveEverything(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        const uint flags = (uint)(SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        // 已置顶时只重挂 TOPMOST（补 NOTOPMOST 会把图钉解掉）。别的程序确实能把置顶窗口压回普通层，
        // 这时靠点击重新挂上，避免「图钉显示勾选、实际不置顶」再也起不来。
        if (window.Topmost)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            return;
        }
        // 未置顶：TOPMOST→NOTOPMOST 一对调用 = 瞬间进置顶带再回普通层最顶，实现「压过霸屏窗口但不留置顶」
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>主窗口的抬层入口：先处理拖拽遗留状态，再按 200ms 节流执行</summary>
    private void RaiseAboveEverything()
    {
        // 拖标签/拖窗格期间抬层会打断停靠反馈，正是之前一闪一闪的成因。
        // 松手事件可能被 Scintilla 子 HWND 吃掉，所以以真实的按键状态为准，别让这个标志永久挡住抬层。
        if (_headerDragActive && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) FlushDeferredActivation();
        if (_headerDragActive) return;

        // 一次点击可能同时来自 Activated/PreviewMouseDown/WndProc 多路，200ms 内只抬一次
        long now = Environment.TickCount64;
        if (now - _lastRaiseTick < 200) return;
        _lastRaiseTick = now;
        RaiseWindowAboveEverything(this);
    }

    /// <summary>给浮动窗挂「被激活就抬层」；与主窗口不同，浮动窗没有窗口级 PreviewMouseDown 兜底</summary>
    /// <param name="window">浮动窗口</param>
    private void HookRaiseOnActivate(Window window)
    {
        if (!_raiseHookedWindows.Add(window)) return;
        window.Activated += (_, _) => RaiseWindowAboveEverything(window);
        window.Closed += (_, _) => _raiseHookedWindows.Remove(window);
    }

    /// <summary>句柄就绪后挂 WndProc 钩子：这是主窗口抬层的「消息级」兜底路径</summary>
    /// <param name="e">基类事件参数，未使用</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(RaiseWndProc);
    }

    /// <summary>需要补抬层的三个鼠标消息：客户区激活点击 / 非客户区按下 / 非客户区双击（标题栏与边框）</summary>
    private const int WM_MOUSEACTIVATE = 0x0021, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONDBLCLK = 0x00A3;

    /// <summary>本窗口已经是前台、只是被别的窗口盖住时不会再触发 Activated，只能从鼠标消息里补抬层</summary>
    /// <param name="hwnd">窗口句柄</param>
    /// <param name="msg">消息编号</param>
    /// <param name="wParam">w 参数（未用）</param>
    /// <param name="lParam">l 参数（未用）</param>
    /// <param name="handled">是否已处理（本钩子不置位）</param>
    /// <returns>恒为 IntPtr.Zero，表示交回默认处理</returns>
    private IntPtr RaiseWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_MOUSEACTIVATE or WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK) RaiseAboveEverything();
        return IntPtr.Zero;   // 不拦截，交给 WPF 正常处理
    }

    /// <summary>浮动窗默认尺寸过小（约 300x200）且可能落在屏幕外，这里放大并夹回工作区</summary>
    /// <param name="window">刚弹出的浮动窗口</param>
    private static void NormalizeFloatingWindow(FloatingWindow window)
    {
        // 最大化/最小化状态下动 Width/Height/Left/Top 无意义，直接跳过
        if (window.WindowState != WindowState.Normal) return;

        // 工作区（去掉任务栏）拿不到合法尺寸时什么都不做，免得把窗口夹成负数
        var work = SystemParameters.WorkArea;
        if (work.Width < 1 || work.Height < 1) return;

        // 目标尺寸：工作区的 55%，但下限 480x340、上限 960x720，小屏不撑爆、大屏不浪费
        double preferredWidth = Math.Min(Math.Max(work.Width * 0.55, 480), 960);
        double preferredHeight = Math.Min(Math.Max(work.Height * 0.55, 340), 720);

        // NaN 表示「没显式设过」，这时用实测值兜底
        double width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        // 夹在 [preferred, 工作区] 之间：至少能看清内容，最多不超出屏幕
        window.Width = Math.Clamp(width, preferredWidth, work.Width);
        window.Height = Math.Clamp(height, preferredHeight, work.Height);

        // 标题栏留在工作区内，否则窗口拖不回来
        double left = double.IsNaN(window.Left) ? work.Left : window.Left;
        double top = double.IsNaN(window.Top) ? work.Top : window.Top;
        window.Left = Math.Clamp(left, work.Left, work.Left + work.Width - window.Width);
        window.Top = Math.Clamp(top, work.Top, work.Top + work.Height - window.Height);
    }

    /// <summary>新文档进当前活动的标签组；活动项在浮动窗口里时回主窗格的标签组</summary>
    /// <returns>可容纳新文档的 DocumentGroup；布局里没有则抛异常</returns>
    private DocumentGroup ActiveDocumentGroup()
    {
        // 活动 View 若就在主窗口的某个 DocumentGroup 里，直接用它，保证新标签开到用户眼前
        var active = ViewManager.Instance.ActiveView;
        if (active != null && !FloatSite.IsFloating(active) && active.Parent is DocumentGroup group)
            return group;

        // 否则回落到布局档案里的主标签组；连标签组都没有属于布局被破坏，抛异常比静默丢文档好
        return DockOperations.GetPrimaryDocumentGroup(_windowProfile!)
            ?? throw new InvalidOperationException("停靠布局里没有文档标签组");
    }

    /// <summary>把一个标签页（布局项 + 编辑器）注册成 VS 停靠 View 并放进当前标签组，选为活动</summary>
    /// <param name="item">标签布局项（持有 Document）</param>
    /// <param name="editor">该标签的编辑器控件</param>
    /// <returns>新建并挂载好的 View</returns>
    private View AttachView(DocumentLayoutItem item, EditorControl editor)
    {
        // View 名字用随机 Guid：停靠档案按名字持久化，避免多次运行间名字撞车
        var view = View.Create(_windowProfile!, "doc_" + Guid.NewGuid().ToString("N"));
        view.Title = item;
        view.Content = editor;
        // 标签头模板（标题 + 脏点 + 锁定/关闭按钮）定义在 XAML 资源里
        view.DocumentTabTitleTemplate = (DataTemplate)FindResource("DocumentTabHeaderTemplate");
        // 文档只能停进文档标签组，拖到边缘时由 VS 拆出新的文档组（而不是工具窗口式的停靠组）
        view.DockRestriction = DockRestrictionType.DocumentGroup;
        view.IsVisibleChanged += OnViewIsVisibleChanged;

        // 双向登记：item↔view，事件里无论拿到哪一侧都能找回另一侧
        _itemViews[item] = view;
        _viewItems[view] = item;

        // 先置可见再加入父级，最后 SetActiveView —— 顺序反了会触发可见性事件里的关闭流程
        view.IsVisible = true;
        ActiveDocumentGroup().Children.Add(view);
        view.IsSelected = true;
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        return view;
    }

    /// <summary>把标签页对应的 View 从停靠树摘除并解除双向登记（不弹保存确认，调用方负责确认）</summary>
    /// <param name="item">要摘除的标签布局项</param>
    private void DetachView(DocumentLayoutItem item)
    {
        if (!_itemViews.TryGetValue(item, out var view)) return;

        // 先退订再 Detach：Detach 会引发 IsVisibleChanged，不摘掉处理器会误触发「隐藏→关文档」递归
        view.IsVisibleChanged -= OnViewIsVisibleChanged;
        view.Detach();
        view.Content = null;
        _viewItems.Remove(view);
        _itemViews.Remove(item);
    }

    /// <summary>取当前活动标签的布局项：优先信 ViewManager，拿不到再退回 _currentDocument 反查</summary>
    /// <returns>活动布局项；没有任何文档时为 null</returns>
    private DocumentLayoutItem? GetActiveItem()
    {
        var view = ViewManager.Instance.ActiveView;
        if (view != null && _viewItems.TryGetValue(view, out var item)) return item;
        return _currentDocument != null ? GetTab(_currentDocument) : null;
    }

    /// <summary>
    /// 实例化全部 ICommand。统一用 RelayCommand（永可执行），实际动作都收敛到本类的私有方法上，
    /// XAML 菜单/工具栏只需 Binding XxxCommand，不写 Click 处理器。
    /// </summary>
    private void InitializeCommands()
    {
        // 文件：新建/打开/保存/另存为
        NewFileCommand = new RelayCommand(NewFile);
        OpenFileCommand = new RelayCommand(OpenFile);
        SaveFileCommand = new RelayCommand(SaveFile);
        SaveAsCommand = new RelayCommand(SaveAs);
        // 编辑基础：撤销重做与剪贴板（剪贴板走 SafeXxx，规避焦点竞态）
        UndoCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.Undo());
        RedoCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.Redo());
        CutCommand = new RelayCommand(() => GetCurrentEditor()?.SafeCut());
        CopyCommand = new RelayCommand(() => GetCurrentEditor()?.SafeCopy());
        PasteCommand = new RelayCommand(() => GetCurrentEditor()?.SafePaste());
        SelectAllCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.SelectAll());
        // 注释三件套与大小写
        LineCommentCommand = new RelayCommand(() => Comment(EditorControl.CommentAction.AddDelLineComment));
        AddBlockCommentCommand = new RelayCommand(() => Comment(EditorControl.CommentAction.AddBlockComment));
        DelBlockCommentCommand = new RelayCommand(() => Comment(EditorControl.CommentAction.DelBlockComment));
        UpperCaseCommand = new RelayCommand(() => ChangeCase(EditorControl.CaseMode.Upper));
        LowerCaseCommand = new RelayCommand(() => ChangeCase(EditorControl.CaseMode.Lower));
        // 查找替换与跳转
        FindCommand = new RelayCommand(ShowFindReplace);
        FindNextCommand = new RelayCommand(FindNext);
        FindPreviousCommand = new RelayCommand(FindPrevious);
        GoToLineCommand = new RelayCommand(GoToLine);
        // 书签
        ToggleBookmarkCommand = new RelayCommand(ToggleBookmark);
        NextBookmarkCommand = new RelayCommand(NextBookmark);
        PreviousBookmarkCommand = new RelayCommand(PreviousBookmark);
        ClearBookmarksCommand = new RelayCommand(ClearBookmarks);
        // 折叠
        CollapseAllCommand = new RelayCommand(() => GetCurrentEditor()?.CollapseAll());
        ExpandAllCommand = new RelayCommand(() => GetCurrentEditor()?.ExpandAll());
        // 视图/主题/面板开关
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleFileTreeCommand = new RelayCommand(ToggleFileTree);
        ToggleMarkdownCommand = new RelayCommand(ToggleMarkdown);
        ToggleBookmarkBarCommand = new RelayCommand(ToggleBookmarkBar);
        ExitCommand = new RelayCommand(() => Close());
        AboutCommand = new RelayCommand(ShowAbout);
        ToggleWordWrapCommand = new RelayCommand(ToggleWordWrap);
        ToggleSpacesCommand = new RelayCommand(ToggleSpaces);
        ToggleLineEndCommand = new RelayCommand(ToggleLineEnd);
        ToggleAllWhiteCommand = new RelayCommand(ToggleAllWhite);
        ToggleToolBarCommand = new RelayCommand(ToggleToolBar);
        ToggleWebAddrCommand = new RelayCommand(ToggleWebAddr);
        // 缩放
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ZoomResetCommand = new RelayCommand(ZoomReset);
        // 批量文件操作：保存全部与各种「关闭 左/右/其他」
        SaveAllCommand = new RelayCommand(SaveAll);
        CloseFileCommand = new RelayCommand(CloseCurrentFile);
        CloseAllCommand = new RelayCommand(CloseAllFiles);
        CloseOtherCommand = new RelayCommand(CloseOtherFiles);
        CloseLeftCommand = new RelayCommand(CloseLeftFiles);
        CloseRightCommand = new RelayCommand(CloseRightFiles);
        // 杂项工具：重载/开目录/日期/统计/开 CMD
        ReloadFileCommand = new RelayCommand(ReloadFile);
        OpenFolderCommand = new RelayCommand(OpenCurrentFileFolder);
        InsertDateCommand = new RelayCommand(InsertDateTime);
        StatsCommand = new RelayCommand(ShowStats);
        OpenCmdCommand = new RelayCommand(OpenCmdInCurrentDir);
        // 书签批量操作（ND2「标记行」一族）
        MarkAllCommand = new RelayCommand(() => MarkAllLines());
        // 格式化：JSON / XML / HTML 转换
        FormatJsonCommand = new RelayCommand(FormatJson);
        MinifyJsonCommand = new RelayCommand(MinifyJson);
        ConvertToHtmlCommand = new RelayCommand(ConvertToHtml);
        FormatXmlCommand = new RelayCommand(FormatXml);
        MinifyXmlCommand = new RelayCommand(MinifyXml);
        ToggleIndentGuideCommand = new RelayCommand(ToggleIndentGuide);
        // 主题与设置目录
        LightThemeCommand = new RelayCommand(() => SetTheme(ThemeService.ThemeMode.Light));
        DarkThemeCommand = new RelayCommand(() => SetTheme(ThemeService.ThemeMode.Dark));
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        // 高亮清理 / 标签锁定 / 置顶 / 位置回退前进
        ClearHighlightCommand = new RelayCommand(() => GetCurrentEditor()?.ClearFindHighlight());
        ToggleTabLockCommand = new RelayCommand(ToggleTabLock);
        ToggleTopmostCommand = new RelayCommand(ToggleTopmost);
        PrePosCommand = new RelayCommand(GotoPrePos);
        NextPosCommand = new RelayCommand(GotoNextPos);
        CutMarkedLinesCommand = new RelayCommand(CutBookmarkedLines);
        CopyMarkedLinesCommand = new RelayCommand(CopyBookmarkedLines);
        PasteMarkedLinesCommand = new RelayCommand(PasteToBookmarkedLines);
        DeleteMarkedLinesCommand = new RelayCommand(DeleteMarkedLines);
        DeleteUnmarkedLinesCommand = new RelayCommand(DeleteUnmarkedLines);
        InvertMarksCommand = new RelayCommand(InvertBookmarks);
    }

    /// <summary>新建未命名文档：默认 UTF-8 无 BOM，名称按「新建 N」自增编号</summary>
    private void NewFile()
    {
        var doc = new Document
        {
            FilePath = string.Empty,
            DisplayName = $"新建 {_newFileCounter + 1}",
            CodePage = EncodingService.CpUtf8,
            HasBom = false,
        };
        _newFileCounter++;
        AddDocument(doc);
    }

    /// <summary>把 SettingsService.Current 里的窗口/主题/面板/显示选项回填到界面（启动时调一次）</summary>
    private void ApplySettingsToWindow()
    {
        var s = SettingsService.Current;

        // 窗口几何：>200 才采纳（防止坏配置把窗口缩没）；位置用 -10000 阈值排除多显示器负坐标以外的脏值
        if (s.WindowWidth > 200) Width = s.WindowWidth;
        if (s.WindowHeight > 200) Height = s.WindowHeight;
        if (s.WindowLeft is double l && s.WindowTop is double t && l > -10000 && t > -10000)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        if (s.Maximized) WindowState = WindowState.Maximized;
        Topmost = s.AlwaysOnTop;

        // 主题先落到 ThemeService，再刷可视树（此刻还没有编辑器，循环体是空的）
        ThemeService.SetTheme(s.Theme == "Dark" ? ThemeService.ThemeMode.Dark : ThemeService.ThemeMode.Light);
        ApplyThemeToUI();

        // 三个可停靠面板的可见性 + 菜单勾选同步
        FileTreePanel.Visibility = s.ShowFileTree ? Visibility.Visible : Visibility.Collapsed;
        MarkdownPanel.Visibility = s.ShowMarkdown ? Visibility.Visible : Visibility.Collapsed;
        BookmarkBarPanel.Visibility = s.ShowBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
        if (s.ShowFileTree) LoadFileTree();

        MenuWordWrap.IsChecked = s.WordWrap;
        MenuIndentGuide.IsChecked = s.ShowIndentGuide;
        MenuFileListView.IsChecked = s.ShowFileTree;

        // 显示工具栏（ND2 :1363-1367：showbar==0 才动）
        MainToolBar.Visibility = s.ShowToolBar ? Visibility.Visible : Visibility.Collapsed;
        MenuShowToolBar.IsChecked = s.ShowToolBar;

        // 显示网站（ND2 :1369-1373：showweb==1 才打开静态位）
        EditorControl.HighlightWebAddr = s.ShowWebAddr;
        MenuShowWebAddr.IsChecked = s.ShowWebAddr;

        // 显示符号三态：ND2 启动时只回填了「显示所有」两个按钮，另外两项漏了（:3199-3211），
        // 存成 1/2 再启动时勾选状态是错的。这里统一回填三处，算刻意偏离。
        SyncBlankShowStatus();

        // 最近文件：只保留磁盘上还存在的；先置 null 再重设 ItemsSource，强制 ListBox 整体重建
        _recentFiles.Clear();
        _recentFiles.AddRange(s.RecentFiles.Where(File.Exists));
        RecentFilesList.ItemsSource = null;
        RecentFilesList.ItemsSource = _recentFiles;
        RefreshRecentFilesMenu();
    }

    /// <summary>重建「文件→最近文件」子菜单：一项一个路径，点击即打开；空列表放一条禁用占位</summary>
    private void RefreshRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "(无)", IsEnabled = false });
            return;
        }
        foreach (var path in _recentFiles)
        {
            var item = new MenuItem { Header = path };
            item.Click += (_, _) => OpenFile(path);
            RecentFilesMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// 窗口级快捷键分发：Scintilla 内部只消化自己的编辑键，其余组合键冒泡到这里。
    /// 每条命中都要置 e.Handled = true，否则 Ctrl+W 之类会被系统/控件再解释一遍。
    /// </summary>
    /// <param name="sender">事件来源（窗口本身），未使用</param>
    /// <param name="e">按键参数；命中后需要置 Handled 阻止继续冒泡</param>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 三个修饰键状态先取出来，下面全部用「组合精确匹配」防止误触（如 Ctrl+Alt+F 与 Ctrl+Shift+F 分开）
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        // 文件与编辑常用
        if (ctrl && !shift && e.Key == Key.N) { NewFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.O) { OpenFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.S) { SaveFile(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { SaveAs(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F) { ShowFindReplace(); e.Handled = true; }
        // 查找/书签导航族（F3/F4/F2/F7/F8 不要求 Ctrl，ND2 同款）
        else if (e.Key == Key.F3 && !shift) { FindNext(); e.Handled = true; }
        else if (e.Key == Key.F3 && shift) { FindPrevious(); e.Handled = true; }
        else if (e.Key == Key.F4 && !ctrl && !alt && !shift) { FindPrevious(); e.Handled = true; }   // ND2 的上一个用 F4
        else if (e.Key == Key.F2 && ctrl && !alt) { ToggleBookmark(); e.Handled = true; }
        else if (e.Key == Key.F2 && shift && !alt) { PreviousBookmark(); e.Handled = true; }
        else if (e.Key == Key.F2 && !ctrl && !alt) { NextBookmark(); e.Handled = true; }
        else if (e.Key == Key.F7 && !ctrl && !alt && !shift) { ClearAllMarks(); e.Handled = true; }
        else if (e.Key == Key.F8 && !ctrl && !alt && !shift) { WordHighlight(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.G) { GoToLine(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.W) { CloseCurrentFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.T) { ToggleTheme(); e.Handled = true; }
        // 格式化与扩展窗口族（Ctrl+Shift / Ctrl+Alt 前缀）
        else if (ctrl && shift && e.Key == Key.F) { FormatJson(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.M) { MinifyJson(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.A) { ShowDirFind(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.R) { ShowCompare(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.R) { ToggleReadOnly(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.H) { ShowHash(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.T) { ToggleTabLock(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.P) { ToggleTopmost(); e.Handled = true; }
        // Ctrl+Alt+Shift+F 必须排在 Ctrl+Alt+F 前面：否则「查找所有打开文件」永远被「文件内查找全部」截胡
        else if (ctrl && alt && shift && e.Key == Key.F) { OnFindAllInOpenedFiles(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.F) { RunFindAllShortcut(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.K) { ShowBatchFind(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.C) { OpenColumnEditor(); e.Handled = true; }
        // 下面四条快捷键与 ND2 的 .ui 一致：Ctrl+D 复制当前行、Ctrl+Shift+↑↓ 移动选区行、Ctrl+Alt+Enter(±Shift) 插空行
        else if (ctrl && !alt && !shift && e.Key == Key.D) { DuplicateCurrentLine(); e.Handled = true; }
        else if (ctrl && shift && !alt && e.Key == Key.Up) { MoveSelectedLinesUp(); e.Handled = true; }
        else if (ctrl && shift && !alt && e.Key == Key.Down) { MoveSelectedLinesDown(); e.Handled = true; }
        else if (ctrl && alt && !shift && e.Key == Key.Return) { InsertBlankLineAbove(); e.Handled = true; }
        else if (ctrl && alt && shift && e.Key == Key.Return) { InsertBlankLineBelow(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.D) { DeleteCurrentLine(); e.Handled = true; }
    }

    /// <summary>
    /// 编辑器内部快捷键的字符串分发（EditorControl.ShortcutPressed 事件送来）：
    /// 这些键 Scintilla 直接吞掉不会冒泡成 KeyDown，只能由编辑器识别后回传命令名。
    /// </summary>
    /// <param name="cmd">编辑器识别出的快捷键命令名（与 HandleShortcut 内的分支字符串对应）</param>
    private void HandleShortcut(string cmd)
    {
        switch (cmd)
        {
            // 文件族
            case "new": NewFile(); break;
            case "open": OpenFile(); break;
            case "save": SaveFile(); break;
            case "saveas": SaveAs(); break;
            // 查找族：走 OnFindXxxRequested 以便先 CommitFindState（面板开着时以面板为准）
            case "find": ShowFindReplace(); break;
            case "findnext": OnFindNextRequested(null, EventArgs.Empty); break;
            case "findprev": OnFindPreviousRequested(null, EventArgs.Empty); break;
            case "gotoline": GoToLine(); break;
            case "closefile": CloseCurrentFile(); break;
            // 书签族
            case "bookmark": ToggleBookmark(); break;
            case "bookmarknext": NextBookmark(); break;
            case "bookmarkprev": PreviousBookmark(); break;
            // 注释三件套
            case "linecom": Comment(EditorControl.CommentAction.AddDelLineComment); break;
            case "bkcom": Comment(EditorControl.CommentAction.AddBlockComment); break;
            case "cbkcom": Comment(EditorControl.CommentAction.DelBlockComment); break;
            // 主题与缩放
            case "theme": ToggleTheme(); break;
            case "zoomin": ZoomIn(); break;
            case "zoomout": ZoomOut(); break;
            case "zoomreset": ZoomReset(); break;
            // 行编辑族
            case "dupline": DuplicateCurrentLine(); break;
            case "movelineup": MoveSelectedLinesUp(); break;
            case "movelinedown": MoveSelectedLinesDown(); break;
            case "blankabove": InsertBlankLineAbove(); break;
            case "blankbelow": InsertBlankLineBelow(); break;
        }
    }

    /// <summary>带过滤器的打开文件对话框（多选），逐个走 OpenFile(path) 流程</summary>
    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*|文本文件 (*.txt)|*.txt|C# 文件 (*.cs)|*.cs|" +
                     "C/C++ 文件 (*.cpp;*.h;*.hpp;*.c)|*.cpp;*.h;*.hpp;*.c|" +
                     "Python (*.py)|*.py|JavaScript (*.js)|*.js|TypeScript (*.ts)|*.ts|" +
                     "JSON (*.json)|*.json|Markdown (*.md)|*.md|XML (*.xml)|*.xml|" +
                     "HTML (*.html;*.htm)|*.html;*.htm|CSS (*.css)|*.css|SQL (*.sql)|*.sql|" +
                     "Shell (*.sh)|*.sh|Go (*.go)|*.go|Rust (*.rs)|*.rs|Java (*.java)|*.java|" +
                     "YAML (*.yaml;*.yml)|*.yaml;*.yml|TOML (*.toml)|*.toml",
            Multiselect = true,
            Title = "打开文件",
        };

        if (dlg.ShowDialog(this) == true)
        {
            // 多选取逐个走完整的单文件打开流程（每个都可能弹 hex/大文件确认，与 ND2 逐文件询问一致）
            foreach (var path in dlg.FileNames)
            {
                OpenFile(path);
            }
        }
    }

    /// <summary>
    /// 打开文件。reloadEncoding 非空表示「以指定编码重新载入」；
    /// quiet 用于启动时恢复会话，不弹任何询问框。
    /// </summary>
    /// <param name="filePath">要打开的绝对路径（已存在文件；新建流程走 NewFile）</param>
    /// <param name="reloadEncoding">「以指定编码重新打开」时传入的 (代码页, 是否 BOM)；普通打开为 null</param>
    /// <param name="quiet">true 表示静默模式（启动恢复会话），不弹大文件/外部变更询问</param>
    private void OpenFile(string filePath, (int CodePage, bool Bom)? reloadEncoding = null, bool quiet = false)
    {
        // 路径无效（空/已被删除）静默返回：启动恢复与最近文件菜单都可能碰到失效路径
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        try
        {
            // 整篇读进内存再解码：编码检测与 BOM 判断都需要原始字节
            byte[] bytes = File.ReadAllBytes(filePath);

            // 已经在标签页里且不是「换编码重载」→ 直接切过去，不重复打开
            var existing = _documents.FirstOrDefault(d => d.FilePath == filePath);
            if (existing != null && reloadEncoding == null)
            {
                SelectDocument(existing);
                return;
            }
            if (existing != null && GetTab(existing) is { } oldTab)
            {
                // 重新载入：丢掉旧标签（不弹保存确认，磁盘内容才是主角）
                CloseTabSilently(oldTab);
            }

            // 已知二进制后缀不问直接进 hex（ND2 ccnotepad.cpp:5417 → doctypelistview.cpp:21）
            if (reloadEncoding == null && BinaryFileTypes.IsHexExtension(filePath))
            {
                OpenHexView(filePath, bytes);
                return;
            }

            // 大文件阈值确认：quiet（启动恢复/换编码重载）不打扰
            var bigFile = SettingsService.Current.BigFileThresholdMb;
            if (!quiet && bytes.Length > bigFile * 1024L * 1024L && !AskOpenLargeFile(filePath, bytes.Length))
                return;

            // 解码：指定编码优先，否则自动检测；DecodeForOpen 会顺带报告「是否乱码」
            var (detectedPage, detectedBom) = reloadEncoding ?? EncodingService.Detect(bytes);
            var openText = EncodingService.DecodeForOpen(bytes, detectedPage, detectedBom);

            // ND2 只在正常打开时询问（用户指定编码重新载入时 hexAsk=false），
            // 判据是整篇按检测编码解不出来（scanFileOutPut 的 existGrbledCode），不看 NUL 数量。
            if (reloadEncoding == null && !quiet && openText.Garbled)
            {
                switch (OpenTextOrHexWindow.Ask(this, filePath))
                {
                    case OpenTextOrHex.Hex:
                        OpenHexView(filePath, bytes);
                        return;
                    case OpenTextOrHex.Cancel:
                        return;
                }
            }

            int codePage = openText.CodePage;
            bool bom = openText.Bom;
            string content = openText.Text;

            // 建文档模型：只读属性、磁盘时间戳/长度快照一并记录，快照用于外部变更比对（见 IsDiskSnapshot）
            var doc = new Document
            {
                FilePath = filePath,
                DisplayName = Path.GetFileName(filePath),
                CodePage = codePage,
                HasBom = bom,
                IsReadOnly = (File.GetAttributes(filePath) & FileAttributes.ReadOnly) != 0,
                DiskTimestamp = File.GetLastWriteTime(filePath),
                DiskLength = bytes.LongLength,
            };

            // AddDocument 建标签+编辑器；随后灌内容、按扩展名挂词法器、应用主题与只读态
            var tab = AddDocument(doc);
            var editor = _itemEditors[tab];

            editor.LoadContent(content);
            editor.SetLexerForFile(filePath);
            editor.ApplyTheme();
            editor.ReadOnly = doc.IsReadOnly;

            // 收尾：状态栏、最近文件、文件树跟随、外部变更监控
            UpdateStatusBar(editor);
            AddRecentFile(filePath);
            RefreshFileTreeForCurrentFile();
            WatchFile(filePath);

            if (reloadEncoding != null)
                StatusBarMessage($"已用 {EncodingService.GetDisplayName(codePage, bom)} 重新载入");
        }
        catch (Exception ex)
        {
            // 读盘/解码任何一步失败都只报错，不留半成品标签
            MessageBox.Show(this, $"打开文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>大文件载入确认；返回 false 表示用户放弃打开</summary>
    /// <param name="filePath">待确认的文件路径（仅用于显示）</param>
    /// <param name="bytes">文件字节数，用于拼装提示文案</param>
    /// <returns>true 表示用户同意继续载入；false 表示放弃</returns>
    private bool AskOpenLargeFile(string filePath, long bytes)
    {
        // MB 保留一位小数展示；Yes 才继续载入
        double mb = bytes / 1024.0 / 1024.0;
        var r = MessageBox.Show(this,
            $"\"{Path.GetFileName(filePath)}\" 体积较大（约 {mb:F1} MB），载入会明显变慢。\n\n是否继续载入？",
            "大文件提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    /// <summary>以只读 hex 转储视图打开文件（Ansi 编码 + null 词法器 + 只读锁死）</summary>
    /// <param name="filePath">文件路径，作为标签标题与「重新按文本打开」的依据</param>
    /// <param name="bytes">已读入内存的原始字节，直接喂给 hex 视图（不再二次读盘）</param>
    private void OpenHexView(string filePath, byte[] bytes)
    {
        var doc = new Document
        {
            FilePath = filePath,
            DisplayName = Path.GetFileName(filePath) + " [hex]",
            CodePage = EncodingService.CpAnsi,
            IsReadOnly = true,
            IsHexView = true,
        };

        // 转储文本整篇渲染（ND2 的 hex 是分页的，这里刻意简化，见 GotoPrePos 的提示）
        var tab = AddDocument(doc);
        var editor = _itemEditors[tab];
        editor.LoadContent(HexDump.FromBytes(bytes));
        editor.SetLexerByName("null");
        editor.ReadOnly = true;
        UpdateStatusBar(editor);
        AddRecentFile(filePath);
    }

    /// <summary>文档进标签体系的唯一入口：建布局项 + 编辑器 + 挂事件 + 登记映射 + AttachView，返回新标签项</summary>
    /// <param name="doc">要展示的文档模型（承载文本、路径、编码、脏标记）</param>
    /// <returns>新建并挂进停靠布局的标签布局项</returns>
    private DocumentLayoutItem AddDocument(Document doc)
    {
        var item = new DocumentLayoutItem(doc);

        var editor = new EditorControl();

        // 应用主题与显示选项
        editor.ApplyTheme();
        var s = SettingsService.Current;
        editor.WordWrap = s.WordWrap;
        editor.BlankShow = s.BlankShow;
        // 缩进线要在装词法器之后才准（ND2 ccnotepad.cpp:4313），SetLexerByName 里还会再补一次
        editor.ShowIndentGuides = s.ShowIndentGuide;

        // 事件：编辑器 → 主窗口的所有回调都在这登记。doc/item/editor 用闭包捕获，天然绑定到本标签
        editor.CursorPositionChanged += (_, _) => UpdateStatusBar(editor);
        editor.ZoomChanged += (_, _) => UpdateStatusBar(editor);
        // 保存点变化 = 脏状态翻转：更新模型、标签头（● 记号）和状态栏
        editor.SavePointChanged += (_, isSaved) =>
        {
            doc.IsDirty = !isSaved;
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
        };
        editor.BookmarkToggled += (_, _) => UpdateBookmarkBar(editor);
        editor.ShortcutPressed += (_, cmd) => HandleShortcut(cmd);
        editor.ReadOnlyEditAttempted += OnReadOnlyEditAttempted;
        // Scintilla 内部的文本拖放若落到文件路径，同样转给统一的拖入打开逻辑
        editor.FilesDropped += paths => OpenDroppedPaths(paths.ToList());

        _documentItems[doc] = item;
        _itemEditors[item] = editor;
        _documents.Add(doc);

        AttachView(item, editor);
        _currentDocument = doc;
        // 有任何文档后隐藏「拖入文件…」占位
        EmptyStateText.Visibility = Visibility.Collapsed;

        editor.FocusEditor();
        return item;
    }

    /// <summary>文档 → 标签布局项的反查；不存在返回 null（文档已关闭）</summary>
    /// <param name="doc">要查询的文档模型</param>
    /// <returns>对应的标签布局项；未挂到任何标签时为 null</returns>
    private DocumentLayoutItem? GetTab(Document doc)
        => _documentItems.TryGetValue(doc, out var item) ? item : null;

    /// <summary>切换活动文档：在它的标签组里选中对应 View 并通知 ViewManager</summary>
    /// <param name="doc">要切为活动的文档；没有对应标签时不做任何事</param>
    private void SelectDocument(Document doc)
    {
        if (!_documentItems.TryGetValue(doc, out var item)) return;
        if (!_itemViews.TryGetValue(item, out var view)) return;

        // 先改父级 SelectedElement（浮动/多组时 view.IsSelected 单独置不生效），再 SetActiveView
        if (view.Parent is ViewGroup parent)
            parent.SelectedElement = view;
        view.IsSelected = true;
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
    }

    /// <summary>窗格结构变动后统一刷新"当前文档"</summary>
    private void SyncCurrentDocument()
    {
        // 先看活动 View 是不是我们的文档；不是（比如焦点在工具窗）就退到最后一个还挂着编辑器的标签
        var view = ViewManager.Instance.ActiveView;
        var item = view != null && _viewItems.TryGetValue(view, out var it) ? it : null;
        if (item == null)
            item = _documentItems.Values.LastOrDefault(x => _itemEditors.ContainsKey(x));

        _currentDocument = item?.Document;
        if (item != null && _itemEditors.TryGetValue(item, out var editor))
        {
            UpdateStatusBar(editor);
            UpdateBookmarkBar(editor);
            RefreshFileTreeForCurrentFile();
        }

        // 后台标签的外部变更等切过来再问；放到 BeginInvoke 里，别卡在停靠布局过程中弹窗
        if (item?.Document.ExternalChangePending == true)
            Dispatcher.BeginInvoke(new Action(AskPendingExternalChange));

        // 一个文档都不剩时显示「拖入文件」占位
        EmptyStateText.Visibility = _documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 关闭一个标签：锁定标签直接拒绝；脏文档弹「保存/放弃/取消」；确认后摘 View、
    /// 解除全部映射并停掉文件监控，最后同步「当前文档」。
    /// </summary>
    /// <param name="item">要关闭的标签布局项（含文档、编辑器、View）</param>
    private void CloseTab(DocumentLayoutItem item)
    {
        var doc = item.Document;

        // 锁定的标签不允许关闭（ND2 的 tabLock 同款保护）
        if (doc.IsLocked)
        {
            MessageBox.Show(this,
                $"标签 \"{doc.DisplayName}\" 已锁定，不能关闭。\n\n用 Ctrl+Alt+T 或「文件 → 锁定标签」解锁。",
                "标签已锁定", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (doc.IsDirty)
        {
            var result = MessageBox.Show(this,
                $"文件 \"{doc.DisplayName}\" 已修改，是否保存？",
                "保存确认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                // SaveFile 只认 _currentDocument，这里临时指过来；保存后仍脏 = 用户在另存为里取消了，放弃关闭
                _currentDocument = doc;
                SaveFile();
                if (doc.IsDirty) return; // 保存失败（用户取消另存为）
            }
        }

        // 真正的拆销：View 从停靠树摘除，四张映射表都清，文件监控停掉
        DetachView(item);
        _documents.Remove(doc);
        _documentItems.Remove(doc);
        _itemEditors.Remove(item);
        if (doc.FilePath.Length > 0) UnwatchFile(doc.FilePath);
        SyncCurrentDocument();
    }

    /// <summary>当前文档对应的编辑器控件；没有打开的文档时返回 null（所有命令的公共前置）</summary>
    /// <returns>活动标签的编辑器控件，或 null</returns>
    private EditorControl? GetCurrentEditor()
    {
        if (_currentDocument != null && _documentItems.TryGetValue(_currentDocument, out var item))
        {
            return _itemEditors.GetValueOrDefault(item);
        }
        return null;
    }

    /// <summary>关闭标签但不询问保存（用于「以指定编码重新载入」等场景）</summary>
    /// <param name="item">要直接丢弃的标签布局项</param>
    private void CloseTabSilently(DocumentLayoutItem item)
    {
        var doc = item.Document;

        if (doc.FilePath.Length > 0) UnwatchFile(doc.FilePath);

        DetachView(item);
        _documents.Remove(doc);
        _documentItems.Remove(doc);
        _itemEditors.Remove(item);
        SyncCurrentDocument();
    }

    // ==================== 状态栏提示 ====================

    /// <summary>临时提示的失效时刻：定时器到点时若现在仍不早于它才清空，防止把后一条提示提前抹掉</summary>
    private DateTime _statusMessageUntil = DateTime.MinValue;

    /// <summary>在状态栏显示一条临时提示（8 秒后被常规信息覆盖）</summary>
    /// <param name="message">提示文本（中文，直接写进状态栏 TextBlock）</param>
    private void StatusBarMessage(string message)
    {
        // 记录失效时刻并用强调色显示
        _statusMessageUntil = DateTime.Now.AddSeconds(8);
        StatusMessage.Text = message;
        StatusMessage.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");

        // 每次调用都起一个新的短命定时器；旧定时器到点发现消息已被顶替（Now &lt; 新失效时刻）就不动手
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (DateTime.Now >= _statusMessageUntil)
            {
                StatusMessage.Text = string.Empty;
                StatusMessage.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            }
        };
        timer.Start();
    }

    // ==================== 外部变更监控 ====================

    /// <summary>文件路径 → FileSystemWatcher；同路径不重复挂（键忽略大小写，与 Windows 文件系统一致）</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    // 弹窗还开着时，同一笔写入的后续事件直接丢掉，不然会叠出好几个框
    /// <summary>正在弹「外部变更」确认框的路径集合（防重入）</summary>
    private readonly HashSet<string> _externalPrompting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>为某个文件挂磁盘写入/删除监控；设置关闭监控或目录不可访问时跳过</summary>
    /// <param name="filePath">要监控的绝对路径（未打开/无路径的文档不挂）</param>
    private void WatchFile(string filePath)
    {
        if (!SettingsService.Current.WatchExternalChanges) return;
        if (_watchers.ContainsKey(filePath)) return;

        // FileSystemWatcher 只能盯「目录+文件名」，目录不存在（如新文件还没落盘）就不挂
        string? dir = Path.GetDirectoryName(filePath);
        string name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir, name)
            {
                // 只关心内容与体积变化；改名/属性变化不算「被外部编辑」
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => BeginExternalChangeCheck(filePath);
            watcher.Deleted += (_, _) => BeginFileDeletedCheck(filePath);
            watcher.EnableRaisingEvents = true;
            _watchers[filePath] = watcher;
        }
        catch
        {
            // 目录被网络共享/权限限制时放弃监控，不影响正常编辑
        }
    }

    /// <summary>解除某个文件的监控并释放 watcher（关标签/另存为改路径时调用）</summary>
    /// <param name="filePath">此前挂过监控的文件路径；未挂过则无副作用</param>
    private void UnwatchFile(string filePath)
    {
        if (_watchers.Remove(filePath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    /// <summary>watcher 事件在非 UI 线程触发，统一转派到调度器上再处理</summary>
    /// <param name="filePath">被报告为「外部改动」的文件路径</param>
    private void BeginExternalChangeCheck(string filePath)
    {
        Dispatcher.BeginInvoke(() => OnExternalFileChanged(filePath));
    }

    /// <summary>文件被外部删除：只报状态栏，不打断编辑（对应 ND2 删除不弹窗、只标记）</summary>
    /// <param name="filePath">被删除的文件路径</param>
    private void BeginFileDeletedCheck(string filePath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var doc = _documents.FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (doc == null) return;
            StatusBarMessage($"磁盘上已删除：{doc.DisplayName}");
        });
    }

    /// <summary>外部写入的判定与分派：快照一致=自己保存的，忽略；后台标签=挂起待问；当前标签=立即询问</summary>
    /// <param name="filePath">watcher 报告变化的文件路径</param>
    private void OnExternalFileChanged(string filePath)
    {
        if (_externalPrompting.Contains(filePath)) return;

        var doc = _documents.FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (doc == null) return;

        try
        {
            if (!File.Exists(filePath))
            {
                StatusBarMessage($"磁盘上已删除：{doc.DisplayName}");
                return;
            }

            // 保存后我们把磁盘时间戳/长度记进了 doc，因此自己那次保存不会弹提示
            if (IsDiskSnapshot(doc, filePath)) return;

            // ND2 只对当前标签立即询问，后台标签先记 Modify_Outside，切过去再问
            if (!ReferenceEquals(doc, _currentDocument))
            {
                doc.ExternalChangePending = true;
                return;
            }

            AskReloadExternal(doc);
        }
        catch
        {
            // 监控本身出错不该打断编辑
        }
    }

    /// <summary>磁盘上的时间戳+长度与文档记录的快照一致 → 判定「磁盘没被别人动过」</summary>
    /// <param name="doc">待比对的文档（持有上次保存的 DiskTimestamp/DiskLength）</param>
    /// <param name="filePath">磁盘路径；文件已不存在时按「不相等」处理</param>
    /// <returns>true 表示磁盘状态与文档快照一致（变化来自本程序自己的保存）</returns>
    private static bool IsDiskSnapshot(Document doc, string filePath)
        => File.GetLastWriteTime(filePath) == doc.DiskTimestamp
           && new FileInfo(filePath).Length == doc.DiskLength;

    /// <summary>外部变更询问（ND2 checkRoladFile 的 Yes[Reload]/No[Drop]；Tailf 按钮随 P4 补）</summary>
    /// <param name="doc">检测到外部改动的文档（其编辑器通过 _documentItems 反查）</param>
    private void AskReloadExternal(Document doc)
    {
        var editor = GetTab(doc) is { } tab ? _itemEditors.GetValueOrDefault(tab) : null;
        if (editor == null) return;

        _externalPrompting.Add(doc.FilePath);
        try
        {
            bool dirty = doc.IsDirty;
            var result = MessageBox.Show(this,
                dirty
                    ? $"\"{doc.DisplayName}\" 在磁盘上已被其他程序修改，且你当前有未保存的改动。\n\n" +
                      "「是」= 放弃你的改动，从磁盘重新载入\n「否」= 保留当前内容，忽略这次变更"
                    : $"\"{doc.DisplayName}\" 在磁盘上已被其他程序修改。\n\n是否重新载入最新内容？",
                "外部变更", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                ReloadFromDisk(doc, editor);
            }
            else
            {
                // 选否就丢掉这次变更（ND2 清 Modify_Outside）；快照对齐后，
                // 同一笔写入引发的后续事件不会再各弹一次
                doc.DiskTimestamp = File.GetLastWriteTime(doc.FilePath);
                doc.DiskLength = new FileInfo(doc.FilePath).Length;
            }

            doc.ExternalChangePending = false;
        }
        catch
        {
            // 文件在弹窗期间又被删掉之类
        }
        finally
        {
            _externalPrompting.Remove(doc.FilePath);
        }
    }

    /// <summary>切到有外部变更的后台标签时补问（ND2 在标签切换里调 checkRoladFile）</summary>
    private void AskPendingExternalChange()
    {
        var doc = _currentDocument;
        if (doc == null || !doc.ExternalChangePending) return;
        // 先清标志再判定：文件可能在挂起期间又被改回去了，此时不该弹窗
        doc.ExternalChangePending = false;

        try
        {
            if (!string.IsNullOrEmpty(doc.FilePath) && File.Exists(doc.FilePath) && !IsDiskSnapshot(doc, doc.FilePath))
                AskReloadExternal(doc);
        }
        catch
        {
        }
    }

    /// <summary>保存当前文档：无路径转另存为；hex 视图拒绝；成功后设保存点、清脏标记、刷标签头与状态栏</summary>
    private void SaveFile()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        // 新文档第一次保存等价于另存为（选路径）
        if (string.IsNullOrEmpty(doc.FilePath))
        {
            SaveAs();
            return;
        }

        // hex 视图内容是转储文本，写回磁盘会毁掉原文件，直接拒绝
        if (doc.IsHexView)
        {
            MessageBox.Show(this, "十六进制视图是只读的，无法保存。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            WriteToDisk(doc, editor.GetContent(), doc.FilePath);
            // SetSavePoint 让 Scintilla 把当前缓冲记为新的「已保存基线」，之后第一次改动才再翻脏
            editor.Scintilla.SetSavePoint();
            doc.IsDirty = false;
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>按文档编码写盘，并同步「磁盘快照」以免外部监控把自己的保存当成别人的改动</summary>
    /// <param name="doc">要落盘的文档（提供编码/BOM 信息并接收新快照）</param>
    /// <param name="content">写入的完整文本内容（编辑器缓冲区原文）</param>
    /// <param name="path">目标路径（另存为时与 doc.FilePath 不同）</param>
    private void WriteToDisk(Document doc, string content, string path)
    {
        EncodingService.WriteFile(path, content, doc.CodePage, doc.HasBom);
        doc.DiskTimestamp = File.GetLastWriteTime(path);
        doc.DiskLength = new FileInfo(path).Length;
    }

    /// <summary>另存为：写盘成功后把文档改挂到新路径（监控/词法器/最近文件全部跟着迁移）</summary>
    private void SaveAs()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "所有文件 (*.*)|*.*|文本文件 (*.txt)|*.txt",
            Title = "另存为",
            FileName = doc.DisplayName,
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            WriteToDisk(doc, editor.GetContent(), dlg.FileName);
            // 先停旧路径的监控再改 doc.FilePath，否则 watcher 回调会拿着新路径找旧文件
            UnwatchFile(doc.FilePath);
            doc.FilePath = dlg.FileName;
            doc.DisplayName = Path.GetFileName(dlg.FileName);
            editor.Scintilla.SetSavePoint();
            doc.IsDirty = false;
            // 换了扩展名词法器要重挂（比如 a.txt 另存成 b.json）
            editor.SetLexerForFile(dlg.FileName);
            editor.ApplyTheme();
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
            AddRecentFile(dlg.FileName);
            WatchFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 编辑操作 ====================

    /// <summary>注释三件套：只读时提示后不动作（ND2 用 isReadOnly 静默拦截，这里与其余格式操作口径一致）</summary>
    /// <param name="action">注释动作（行注释 / 块注释 / 取消块注释），由编辑器实现</param>
    private void Comment(EditorControl.CommentAction action)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        editor.Comment(action);
    }

    // ==================== 格式菜单（行/列/大小写批量操作） ====================

    /// <summary>格式类操作的前提：有编辑器、非只读、非十六进制视图</summary>
    /// <returns>可安全执行格式操作的编辑器；条件不满足（已提示）时返回 null</returns>
    private EditorControl? FormatEditor()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return null;

        // 闸门 1：hex 视图展示的是转储文本，任何行级改写都会毁掉语义
        if (doc.IsHexView)
        {
            StatusBarMessage("十六进制视图不支持格式操作");
            return null;
        }
        // 闸门 2：只读文档不动内容，只在状态栏提示快捷键
        if (doc.IsReadOnly)
        {
            StatusBarMessage("文档处于只读模式（Ctrl+Alt+R 取消）");
            return null;
        }
        return editor;
    }

    /// <summary>按指定模式排序文本行（ND2 dealLineSort 家族，模式见 EditorControl.SortMode）</summary>
    /// <param name="mode">排序模式（升/降/忽略大小写/按长度/按数字等）</param>
    private void SortLines(EditorControl.SortMode mode) => FormatEditor()?.SortLines(mode);
    /// <summary>删除相邻的重复行（只删连续相同的，非相邻不动）</summary>
    private void RemoveConsecutiveDuplicateLines() => FormatEditor()?.RemoveConsecutiveDuplicateLines();
    /// <summary>删除重复行（全文去重，保序）</summary>
    private void RemoveDuplicateLines() => FormatEditor()?.RemoveDuplicateLines();
    /// <summary>去重并顺带排序</summary>
    private void RemoveDuplicateLinesSorted() => FormatEditor()?.RemoveDuplicateLinesSorted();

    /// <summary>
    /// ND2「移除空行」/「移除空行(包括空白字符)」（ccnotepad.cpp:9137/9143）：
    /// 都是静默调用替换所有，所以文案也照 findwin.cpp:2367 的状态栏那句。
    /// </summary>
    /// <param name="containingBlankChars">true 表示连「只含空白字符的行」也算空行一并删除</param>
    private void RemoveEmptyLines(bool containingBlankChars)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        int count = editor.RemoveEmptyLines(containingBlankChars);
        StatusBarMessage($"替换完成，一共 {count} 处替换！");
    }

    /// <summary>删除文档开头和结尾的空行</summary>
    private void RemoveHeadEndEmptyLines() => FormatEditor()?.RemoveHeadEndEmptyLines();
    /// <summary>把连续多个空行压成一个</summary>
    private void RemoveConsecutiveEmptyLines() => FormatEditor()?.RemoveConsecutiveEmptyLines();
    /// <summary>去除各行行首空白并报告替换次数</summary>
    private void RemoveHeadBlank()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        StatusBarMessage($"替换完成，一共 {editor.RemoveLineHeadBlank()} 处替换！");
    }

    /// <summary>去除各行行尾空白并报告替换次数</summary>
    private void RemoveEndBlank()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        StatusBarMessage($"替换完成，一共 {editor.RemoveLineEndBlank()} 处替换！");
    }

    /// <summary>行首行尾空白一起去（返回值元组里只用行尾计数，理由见下）</summary>
    private void RemoveHeadEndBlank()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        var (_, end) = editor.RemoveLineHeadEndBlank();
        // ND2 是连着调两次 slot_replaceAll，两条状态栏消息后一条盖掉前一条，所以这里只报行尾那次的数
        StatusBarMessage($"替换完成，一共 {end} 处替换！");
    }

    /// <summary>在当前行上方插一个空行（Ctrl+Alt+Enter）</summary>
    private void InsertBlankLineAbove() => FormatEditor()?.InsertBlankLineAbove();
    /// <summary>在当前行下方插一个空行（Ctrl+Alt+Shift+Enter）</summary>
    private void InsertBlankLineBelow() => FormatEditor()?.InsertBlankLineBelow();
    /// <summary>复制当前整行到下一行（Ctrl+D）</summary>
    private void DuplicateCurrentLine() => FormatEditor()?.DuplicateCurrentLine();
    /// <summary>选区的各行整体上移一行（Ctrl+Shift+↑）</summary>
    private void MoveSelectedLinesUp() => FormatEditor()?.MoveSelectedLinesUp();
    /// <summary>选区的各行整体下移一行（Ctrl+Shift+↓）</summary>
    private void MoveSelectedLinesDown() => FormatEditor()?.MoveSelectedLinesDown();
    /// <summary>删除当前行（Ctrl+Shift+D）</summary>
    private void DeleteCurrentLine() => FormatEditor()?.DeleteCurrentLine();
    /// <summary>删除从光标到行首的内容</summary>
    private void DeleteToLineStart() => FormatEditor()?.DeleteToLineStart();
    /// <summary>删除从光标到行尾的内容</summary>
    private void DeleteToLineEnd() => FormatEditor()?.DeleteToLineEnd();
    /// <summary>剪切整行（连同换行符一起进剪贴板）</summary>
    private void CutCurrentLine() => FormatEditor()?.CutCurrentLine();
    /// <summary>复制整行到剪贴板（只读文档也允许，走 GetCurrentEditor 不过格式闸门）</summary>
    private void CopyCurrentLine() => GetCurrentEditor()?.CopyCurrentLine();
    /// <summary>复制光标到行尾的内容到剪贴板</summary>
    private void CopyToLineEnd() => GetCurrentEditor()?.CopyToLineEnd();
    /// <summary>把软/硬换行拆开选区的多行合并为一行</summary>
    private void JoinLines() => FormatEditor()?.JoinLines();
    /// <summary>把选区/文档中的长行按策略拆成多行</summary>
    private void SplitLines() => FormatEditor()?.SplitLines();
    /// <summary>把选区里的字面 \n 转义序列展开成真实换行</summary>
    private void ExpandEscapedNewlines() => FormatEditor()?.ExpandEscapedNewlines();
    /// <summary>各行为对齐补空格到相同宽度（制表对齐用）</summary>
    private void PadLinesToEqualWidth() => FormatEditor()?.PadLinesToEqualWidth();

    /// <summary>按模式做大小写转换（大写/小写/词首大写/句首大写/互换/随机）</summary>
    /// <param name="mode">目标大小写模式；无编辑器或只读时 FormatEditor 已拦下</param>
    private void ChangeCase(EditorControl.CaseMode mode) => FormatEditor()?.ChangeCase(mode);

    /// <summary>TAB 全部转成空格（按 Tab 设置宽度展开）</summary>
    private void TabsToSpaces() => FormatEditor()?.TabsToSpaces();

    /// <summary>空格转 TAB；leadingOnly=true 时只处理行首缩进</summary>
    /// <param name="leadingOnly">true 表示只转行首空白（菜单「行首空格转 TAB」），false 全文转换</param>
    private void SpacesToTabsMenu(bool leadingOnly) => FormatEditor()?.SpacesToTabs(leadingOnly);

    /// <summary>全文转换行尾符风格，转换后立刻刷新状态栏的 EOL 显示</summary>
    /// <param name="eol">目标行尾符（CRLF / LF / CR），来自菜单项</param>
    private void ConvertEol(ScintillaNET.Eol eol)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        editor.ConvertEol(eol);
        UpdateStatusBar(editor);
    }

    /// <summary>列编辑器对话框：先决条件是有选区（插入列位置取选区左边界），确认后按参数逐行写入并落盘设置</summary>
    private void OpenColumnEditor()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        if (editor.SelectionLength == 0)
        {
            MessageBox.Show(this, "请先选中要处理的多行（插入列位置取选区左边界所在的列）。", "列编辑器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ColumnEditorWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // 八项参数全部来自对话框：模式（文本/数字）、起始值、步长、重复次数、位数、数字格式、前导零、前缀
        editor.ColumnEditor(dlg.Mode, dlg.Text, dlg.Initial, dlg.Increment, dlg.Repeat,
                            dlg.Digits, dlg.NumberFormat, dlg.LeadingZero, dlg.Prefix);
        SettingsService.Save();
    }

    /// <summary>列（矩形）编辑模式开关：开启后鼠标拖选即为矩形选区</summary>
    private void ToggleColumnMode()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ColumnMode = !editor.ColumnMode;
        _menuColumnMode.IsChecked = editor.ColumnMode;
        StatusBarMessage(editor.ColumnMode ? "列编辑模式已开启：拖选即为矩形选区" : "列编辑模式已关闭");
    }

    /// <summary>当前文档只读态开关（Ctrl+Alt+R）：模型、编辑器、菜单勾选、状态栏四处同步</summary>
    private void ToggleReadOnly()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        doc.IsReadOnly = !doc.IsReadOnly;
        editor.ReadOnly = doc.IsReadOnly;
        MenuReadOnly.IsChecked = doc.IsReadOnly;
        UpdateStatusBar(editor);
    }

    /// <summary>在光标所在行寻找 URL/邮箱并用系统浏览器打开（ND2 热点 Ctrl+单击的同逻辑入口）</summary>
    private void OpenUrlAtCaret()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!editor.OpenUrlAtCaret()) StatusBarMessage("光标所在行没有找到 URL 或邮箱");
    }

    /// <summary>用当前文档词表做自动补全弹窗（Ctrl+Space）</summary>
    private void AutoCompleteFromDocument() => GetCurrentEditor()?.AutoCompleteFromDocument();

    // 书签的批量操作：读操作（复制）任何文档都能用，写操作走 FormatEditor 的只读/十六进制闸门
    /// <summary>复制所有被标记行到剪贴板</summary>
    private void CopyBookmarkedLines() => GetCurrentEditor()?.CopyMarkedLines();
    /// <summary>剪切所有被标记行</summary>
    private void CutBookmarkedLines() => FormatEditor()?.CutMarkedLines();
    /// <summary>把剪贴板逐行粘贴到每个被标记行的行首</summary>
    private void PasteToBookmarkedLines() => FormatEditor()?.PasteToMarkedLines();
    /// <summary>删除所有被标记行（marked=true）</summary>
    private void DeleteMarkedLines() => FormatEditor()?.DeleteMarkedLines(true);
    /// <summary>删除所有未被标记的行（marked=false，即只保留标记行）</summary>
    private void DeleteUnmarkedLines() => FormatEditor()?.DeleteMarkedLines(false);
    /// <summary>反选书签：有书签的行去掉、没有的行加上</summary>
    private void InvertBookmarks() => FormatEditor()?.ToggleBookmarksOthers();

    /// <summary>「列编辑模式」菜单项的引用：BuildFormatMenu 创建，ToggleColumnMode/UpdateStatusBar 回填勾选态</summary>
    private MenuItem _menuColumnMode = null!;

    /// <summary>
    /// 用代码生成「格式」菜单：项目里同类菜单太长，XAML 写 40 个 Click 处理器不划算，
    /// 而这里的项目惯例（编码/语言菜单）也是代码生成的。
    /// </summary>
    private void BuildFormatMenu()
    {
        FormatMenu.Items.Clear();

        // 「列编辑模式」是勾选项且要被别处回填状态，所以单独建成员变量
        _menuColumnMode = new MenuItem { Header = "列编辑模式", IsCheckable = true };
        _menuColumnMode.Click += (_, _) => ToggleColumnMode();

        // 局部函数：造一条普通菜单项（文案 + 快捷键回显文字 + 点击动作）
        /// <summary>构造一条普通菜单项：设置文案、快捷键回显文字与点击动作</summary>
        /// <param name="header">菜单显示文案（含「…」后缀表示会弹对话框）</param>
        /// <param name="action">点击后执行的动作</param>
        /// <param name="gesture">右侧灰字回显的快捷键文本（留空则不显示）</param>
        /// <returns>可直接 Add 进父菜单的 MenuItem</returns>
        static MenuItem Item(string header, Action action, string gesture = "")
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }
        // 局部函数：分隔线（写起来短一点，下面用得多）
        /// <summary>造一条菜单分隔线</summary>
        /// <returns>新的 Separator，直接 Add 进父菜单</returns>
        static Separator Sep() => new();

        // 「行编辑」照 ND2 的 menuLine_Operations（ccnotepad.ui:147-167）：15 项、顺序、文案、快捷键逐条对齐。
        // 数值排序/随机行序/连续去重在 ND2 是 .ui 里没接线的死控件，全部挪进后面的「更多行操作」。
        var lineEditMenu = new MenuItem { Header = "行编辑(_L)" };
        lineEditMenu.Items.Add(Item("复制当前行", DuplicateCurrentLine, "Ctrl+D"));
        lineEditMenu.Items.Add(Item("删除重复行", RemoveDuplicateLines));
        lineEditMenu.Items.Add(Item("分割行", SplitLines));
        lineEditMenu.Items.Add(Item("合并行", JoinLines));
        lineEditMenu.Items.Add(Item("上移当前行", MoveSelectedLinesUp, "Ctrl+Shift+↑"));
        lineEditMenu.Items.Add(Item("下移当前行", MoveSelectedLinesDown, "Ctrl+Shift+↓"));
        lineEditMenu.Items.Add(Item("移除空行", () => RemoveEmptyLines(false)));
        lineEditMenu.Items.Add(Item("移除空行(包括空白字符)", () => RemoveEmptyLines(true)));
        lineEditMenu.Items.Add(Item("在当前行上方插入空行", InsertBlankLineAbove, "Ctrl+Alt+Enter"));
        lineEditMenu.Items.Add(Item("在当前行下方插入空行", InsertBlankLineBelow, "Ctrl+Alt+Shift+Enter"));
        lineEditMenu.Items.Add(Item("反排序行", () => SortLines(EditorControl.SortMode.ReverseOrder)));
        lineEditMenu.Items.Add(Sep());
        lineEditMenu.Items.Add(Item("升序排列文本行", () => SortLines(EditorControl.SortMode.LexAsc)));
        lineEditMenu.Items.Add(Item("升序排列文本行（不分大小写）", () => SortLines(EditorControl.SortMode.LexAscIgnoreCase)));
        lineEditMenu.Items.Add(Item("降序排列文本行", () => SortLines(EditorControl.SortMode.LexDesc)));
        lineEditMenu.Items.Add(Item("降序排列文本行（不分大小写）", () => SortLines(EditorControl.SortMode.LexDescIgnoreCase)));
        FormatMenu.Items.Add(lineEditMenu);

        // 「大小写转换」照 menuConvert_Case_to（ccnotepad.ui:134-146）：8 项，ND2 一个快捷键都没给
        var caseMenu = new MenuItem { Header = "大小写转换(_C)" };
        caseMenu.Items.Add(Item("转成大写", () => ChangeCase(EditorControl.CaseMode.Upper)));
        caseMenu.Items.Add(Item("转成小写", () => ChangeCase(EditorControl.CaseMode.Lower)));
        caseMenu.Items.Add(Item("每词转成仅首字母大写", () => ChangeCase(EditorControl.CaseMode.Proper)));
        caseMenu.Items.Add(Item("每词的首字母转成大写", () => ChangeCase(EditorControl.CaseMode.ProperBlend)));
        caseMenu.Items.Add(Item("每句转成仅首字母大写", () => ChangeCase(EditorControl.CaseMode.Sentence)));
        caseMenu.Items.Add(Item("每句的首字母转成大写", () => ChangeCase(EditorControl.CaseMode.SentenceBlend)));
        caseMenu.Items.Add(Item("大小写互换", () => ChangeCase(EditorControl.CaseMode.Invert)));
        caseMenu.Items.Add(Item("随机大小写", () => ChangeCase(EditorControl.CaseMode.Random)));
        FormatMenu.Items.Add(caseMenu);

        // 「空白字符操作」照 ND2 menuBlank_CharOperate（ccnotepad.ui:122-133）：顺序 TAB 转空格在前、
        // 空格转 TAB (全部) / (行首) 在后，6 项一个快捷键都没有（zh.ts:1615/1621/1627/1723/1729/1735 的文案原样抄）
        var blankCharMenu = new MenuItem { Header = "空白字符操作(_W)" };
        blankCharMenu.Items.Add(Item("去除行首空白", RemoveHeadBlank));
        blankCharMenu.Items.Add(Item("去除行尾空白", RemoveEndBlank));
        blankCharMenu.Items.Add(Item("去除行首尾空白", RemoveHeadEndBlank));
        blankCharMenu.Items.Add(Sep());
        blankCharMenu.Items.Add(Item("TAB 转空格", TabsToSpaces));
        blankCharMenu.Items.Add(Item("空格转 TAB (全部)", () => SpacesToTabsMenu(leadingOnly: false)));
        blankCharMenu.Items.Add(Item("空格转 TAB (行首)", () => SpacesToTabsMenu(leadingOnly: true)));
        FormatMenu.Items.Add(blankCharMenu);

        var moreLineMenu = new MenuItem { Header = "更多行操作(_M)" };
        // 「更多行操作」收纳 ND2 里没接线的死控件 + 我们新增的实用项（数值排序/随机/补齐宽度等）
        moreLineMenu.Items.Add(Item("展开选区里的 \\n 转义", ExpandEscapedNewlines));
        moreLineMenu.Items.Add(Item("删除相邻重复行", RemoveConsecutiveDuplicateLines));
        moreLineMenu.Items.Add(Item("删除重复行（去重后排序）", RemoveDuplicateLinesSorted));
        moreLineMenu.Items.Add(Item("连续空行压成一行", RemoveConsecutiveEmptyLines));
        moreLineMenu.Items.Add(Item("删除首尾空行", RemoveHeadEndEmptyLines));
        moreLineMenu.Items.Add(Sep());
        moreLineMenu.Items.Add(Item("按整数升序", () => SortLines(EditorControl.SortMode.IntegerAsc)));
        moreLineMenu.Items.Add(Item("按整数降序", () => SortLines(EditorControl.SortMode.IntegerDesc)));
        moreLineMenu.Items.Add(Item("按小数升序（小数点）", () => SortLines(EditorControl.SortMode.DecimalDotAsc)));
        moreLineMenu.Items.Add(Item("按小数降序（小数点）", () => SortLines(EditorControl.SortMode.DecimalDotDesc)));
        moreLineMenu.Items.Add(Item("按小数升序（逗号）", () => SortLines(EditorControl.SortMode.DecimalCommaAsc)));
        moreLineMenu.Items.Add(Item("按小数降序（逗号）", () => SortLines(EditorControl.SortMode.DecimalCommaDesc)));
        moreLineMenu.Items.Add(Item("随机打乱行", () => SortLines(EditorControl.SortMode.Randomize)));
        moreLineMenu.Items.Add(Sep());
        moreLineMenu.Items.Add(Item("删除当前行", DeleteCurrentLine, "Ctrl+Shift+D"));
        moreLineMenu.Items.Add(Item("剪切到行尾", CutCurrentLine));
        moreLineMenu.Items.Add(Item("复制到行尾", CopyToLineEnd));
        moreLineMenu.Items.Add(Item("复制到整行", CopyCurrentLine));
        moreLineMenu.Items.Add(Item("删除到行首", DeleteToLineStart));
        moreLineMenu.Items.Add(Item("删除到行尾", DeleteToLineEnd));
        moreLineMenu.Items.Add(Item("各行补齐到相同宽度", PadLinesToEqualWidth));
        FormatMenu.Items.Add(moreLineMenu);

        // 「行尾符」：Windows/Mac/Unix 三种 EOL 全文转换（ND2 Encoding 菜单同款三项）
        var eolMenu = new MenuItem { Header = "行尾符(_E)" };
        eolMenu.Items.Add(Item("Windows: CR LF", () => ConvertEol(ScintillaNET.Eol.CrLf)));
        eolMenu.Items.Add(Item("Mac: CR", () => ConvertEol(ScintillaNET.Eol.Cr)));
        eolMenu.Items.Add(Item("Unix: LF", () => ConvertEol(ScintillaNET.Eol.Lf)));
        FormatMenu.Items.Add(eolMenu);


        // 分隔线后是「多光标/多行」一族与 JSON/XML 格式化子菜单，最后是两条零散工具项
        FormatMenu.Items.Add(Sep());
        FormatMenu.Items.Add(_menuColumnMode);
        FormatMenu.Items.Add(Item("列编辑器…", OpenColumnEditor, "Alt+C"));
        FormatMenu.Items.Add(Item("给选中各行行首加光标", () => GetCurrentEditor()?.AddCaretsToLineStarts()));
        FormatMenu.Items.Add(Item("把选区粘贴到每一行行首", () => GetCurrentEditor()?.PasteToEachSelectedLine()));
        FormatMenu.Items.Add(Sep());
        var formatLangMenu = new MenuItem { Header = "格式化(_F)" };
        formatLangMenu.Items.Add(Item("格式化 JSON", FormatJson, "Ctrl+Shift+F"));
        formatLangMenu.Items.Add(Item("压缩 JSON", MinifyJson, "Ctrl+Shift+M"));
        formatLangMenu.Items.Add(Item("格式化 XML", FormatXml));
        formatLangMenu.Items.Add(Item("压缩 XML", MinifyXml));
        FormatMenu.Items.Add(formatLangMenu);

        FormatMenu.Items.Add(Item("打开光标处的链接", OpenUrlAtCaret));
        FormatMenu.Items.Add(Item("按文档词表自动补全", AutoCompleteFromDocument, "Ctrl+Space"));
    }

    // ==================== 查找替换 ====================

    /// <summary>打开查找替换面板（Ctrl+F）：选区是单行短文本时预填选区，否则预填上次的查找词</summary>
    private void ShowFindReplace()
    {
        FindReplacePanel.Visibility = Visibility.Visible;

        var editor = GetCurrentEditor();
        if (editor == null) return;

        // 预填策略与 ND2 相同：带换行或超长（200+）的选区不适合当查找词，忽略
        var sel = editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && sel.Length < 200 && !sel.Contains('\n'))
            FindReplacePanel.SetFindText(sel);
        else
            FindReplacePanel.SetFindText(_lastFind.Text);
    }

    /// <summary>
    /// 把面板上的条件落成宿主状态：面板开着时每次动作都同步一次，
    /// 关掉面板后 F3/F4 仍沿用最后一次条件（ND2 的行为）。
    /// </summary>
    private void CommitFindState()
    {
        if (FindReplacePanel.Visibility != Visibility.Visible) return;
        _lastFind = FindReplacePanel.CurrentFind;
        _lastReplace = FindReplacePanel.CurrentReplace;
        _wrapAround = FindReplacePanel.WrapAround;
        _backwardSearch = FindReplacePanel.Backward;
        // 顺手进下拉历史（ND2 的 QCompleter 历史同源）
        FindReplacePanel.Remember(_lastFind.Text, _lastReplace);
    }

    /// <summary>面板「查找下一个」按钮：先同步面板条件再查</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnFindNextRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        FindNext();
    }

    /// <summary>面板「查找上一个」按钮：先同步面板条件再反向查</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnFindPreviousRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        FindPrevious();
    }

    /// <summary>ND2「计数(T)」：结果写状态栏（"计数 %1 次匹配 '%2'"），找不到时蜂鸣</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnCountRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null || _lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            return;
        }
        int count = editor.CountMatches(_lastFind);
        FindReplacePanel.SetMatchCount(count);
        StatusBarMessage($"计数 {count} 次匹配 '{_lastFind.Text}'");
        if (count == 0) System.Media.SystemSounds.Beep.Play();
    }

    /// <summary>菜单「在当前文件中计数」的 Click 转发（RoutedEventArgs 派生自 EventArgs，可直接复用）</summary>
    /// <param name="sender">菜单项，转发给面板计数逻辑（未使用）</param>
    /// <param name="e">RoutedEventArgs，原样转发</param>
    private void OnCountInFile(object sender, RoutedEventArgs e) => OnCountRequested(sender, e);

    /// <summary>替换当前命中处然后自动找下一个；查找词为空时蜂鸣提示</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnReplaceRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        editor.ReplaceCurrent(_lastFind, _lastReplace);
        FindNext();
    }

    /// <summary>当前文件全部替换：先确认，结果只写状态栏（ND2 findwin.cpp:2324 口径）</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnReplaceAllRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        // ND2 replaceAll（findwin.cpp:2324）：先确认，替换完只写状态栏，不弹结果框
        if (MessageBox.Show(this, "是否确认在当前打开的文档中替换？", "在当前文件中替换所有",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int count = editor.ReplaceAll(_lastFind, _lastReplace);
        // 替换完的剩余命中数回填面板计数框
        FindReplacePanel.SetMatchCount(editor.CountMatches(_lastFind));
        StatusBarMessage($"替换完成，一共 {count} 处替换！");
    }

    /// <summary>
    /// ND2 slot_replaceAllInOpenDoc（findwin.cpp:2379）：确认框 → 逐个文档全部替换，
    /// 只读文档跳过，最后按「在打开的文档中替换：%1 处已经被替换。」汇报。
    /// </summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnReplaceAllInOpenedRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        if (MessageBox.Show(this, "是否确认在所有打开的文档中替换？", "替换所有打开的文档",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int replaced = 0;
        int touched = 0;
        foreach (var doc in _documents)
        {
            var editor = GetEditor(doc);
            if (editor == null || editor.ReadOnly) continue;      // 只读的文档不能替换
            int count = editor.ReplaceAll(_lastFind, _lastReplace);
            if (count > 0) touched++;
            replaced += count;
        }
        StatusBarMessage($"在打开的文档中替换：{replaced} 处已经被替换。（{touched} 个文件）");
    }

    /// <summary>Mark 面板「标记全部」：用当前轮色槽（或面板选定色槽）高亮所有命中，withBookmark=true 时同时加书签</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="withBookmark">true 表示「标记并加书签」（菜单项与面板按钮共用本方法）</param>
    private void OnMarkRequested(object? sender, bool withBookmark)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("标记字段为空！");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        // 色槽由面板轮转给出（ND2 的 5 色轮转标记）
        int slot = FindReplacePanel.TakeMarkSlot();
        int count = withBookmark ? editor.MarkAllWithBookmark(_lastFind, slot) : editor.MarkAll(_lastFind, slot);
        FindReplacePanel.SetMatchCount(count);
        StatusBarMessage(withBookmark ? $"已标记 {count} 处并加书签" : $"已标记 {count} 处");
    }

    /// <summary>ND2 Mark tab 的 Clear Mark：只清当前这个词的标记</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnClearMarkRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("标记字段为空");
            return;
        }
        int cleared = editor.ClearMarkOf(_lastFind.Text);
        StatusBarMessage($"已清除「{_lastFind.Text}」的 {cleared} 处标记");
    }

    /// <summary>ND2 的 Clear All / 工具栏「清除所有高亮」(F7)：五种标记色一起清</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnClearAllMarkRequested(object? sender, EventArgs e)
    {
        GetCurrentEditor()?.ClearAllMarks();
        StatusBarMessage("已清除全部高亮");
    }

    /// <summary>F7/快捷键路径的清高亮：与 OnClearAllMarkRequested 同动作，只是入口不同</summary>
    private void ClearAllMarks()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ClearAllMarks();
        StatusBarMessage("已清除全部高亮");
    }

    /// <summary>ND2 工具栏「单词高亮」(F8)：选中就用选区，否则光标处的词；再按一次取消</summary>
    /// <param name="fixedSlot">固定色槽索引（来自「标记颜色」子菜单）；null 表示用当前轮转色槽</param>
    private void WordHighlight(int? fixedSlot = null)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        // 从「标记颜色」菜单过来时用指定色，不吃轮色；轮色只属于 Mark 面板那套
        int slot = fixedSlot ?? FindReplacePanel.TakeMarkSlot();
        int count = editor.ToggleWordMark(slot);
        StatusBarMessage(count == 0 ? "已取消当前单词的高亮" : $"已高亮当前单词：{count} 处");
    }

    /// <summary>菜单/工具栏「单词高亮」的 Click 转发</summary>
    /// <param name="sender">菜单/工具栏项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用（色槽由 WordHighlight 自己轮转）</param>
    private void OnWordHighlight(object sender, RoutedEventArgs e) => WordHighlight();

    /// <summary>工具栏「清除所有高亮」的 Click 转发</summary>
    /// <param name="sender">工具栏按钮，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnClearAllHighlight(object sender, RoutedEventArgs e) => ClearAllMarks();

    /// <summary>
    /// 「查找→标记颜色」：照 ND2 的 QActionGroup（ccnotepad.cpp:2130-2171），5 个色块单选，
    /// 并且选完立刻用这个色重标光标处的词（slot_markColorGroup 末尾直接调 slot_wordHighlight）。
    /// </summary>
    private void BuildMarkColorMenu()
    {
        for (int i = 0; i < EditorControl.MarkColorCount; i++)
        {
            int slot = i + 1;
            var c = EditorControl.MarkColors[i];
            var item = new MenuItem
            {
                Header = $"颜色 {slot}",
                IsCheckable = true,
                IsChecked = SettingsService.Current.MarkColorSlot == slot,
                Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 16,
                    Height = 10,
                    Margin = new Thickness(2, 0, 4, 0),
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                        (byte)(c >> 16), (byte)(c >> 8), (byte)c)),
                },
            };

            item.Click += (_, _) =>
            {
                // WPF 的 MenuItem 没有 QActionGroup 的互斥，勾要自己收
                foreach (object obj in MarkColorMenu.Items)
                {
                    if (obj is MenuItem mi) mi.IsChecked = ReferenceEquals(mi, item);
                }
                FindReplacePanel.SelectMarkColorSlot(slot);
                WordHighlight(slot);
            };
            MarkColorMenu.Items.Add(item);
        }
    }

    /// <summary>面板关闭按钮：收起面板、清掉查找高亮并把焦点还给编辑器</summary>
    /// <param name="sender">查找替换面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnFindCloseRequested(object? sender, EventArgs e)
    {
        FindReplacePanel.Visibility = Visibility.Collapsed;
        var editor = GetCurrentEditor();
        editor?.ClearFindHighlight();
        editor?.FocusEditor();
    }

    // ==================== 目录查找 / 替换 ====================

    /// <summary>打开目录查找面板（Ctrl+Shift+A）：默认根目录取当前文件所在目录，单行短选区预填关键词</summary>
    private void ShowDirFind()
    {
        var doc = _currentDocument;
        string root = doc?.FilePath.Length > 0
            ? Path.GetDirectoryName(doc.FilePath) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;

        // 预填词限制在 100 字符内（比查找面板的 200 严，目录级关键词一般很短）
        string selected = GetCurrentEditor()?.SelectedText ?? string.Empty;
        DirFindPanel.Visibility = Visibility.Visible;
        DirFindPanel.Prepare(root, selected.Length is > 0 and < 100 ? selected : null);
    }

    /// <summary>菜单「目录中查找」的 Click 转发</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnShowDirFind(object sender, RoutedEventArgs e) => ShowDirFind();

    /// <summary>目录查找面板关闭：收起并把焦点还给编辑器</summary>
    /// <param name="sender">目录查找面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnDirFindCloseRequested(object? sender, EventArgs e)
    {
        DirFindPanel.Visibility = Visibility.Collapsed;
        GetCurrentEditor()?.FocusEditor();
    }

    /// <summary>双击结果：必要时先打开文件，再选中文本并滚动到可见处</summary>
    /// <param name="sender">目录查找面板的事件源，未使用</param>
    /// <param name="match">被双击的那条命中记录（文件路径 + 行号 + 列范围）</param>
    private void OnDirMatchJumpRequested(object? sender, DirMatch match)
    {
        // 路径忽略大小写匹配已开标签（Windows 文件系统不区分大小写）
        Document? doc = _documentItems.Keys.FirstOrDefault(
            d => string.Equals(d.FilePath, match.FilePath, StringComparison.OrdinalIgnoreCase));

        if (doc == null)
        {
            // 没开过 → 走完整打开流程；OpenFile 成功后 _currentDocument 即新文档
            OpenFile(match.FilePath);
            doc = _currentDocument;
        }
        else
        {
            SelectDocument(doc);
        }

        // 行号转 0 基：结果里的 Line 是给人看的 1 基
        var editor = doc == null ? null : GetEditor(doc);
        if (editor == null) return;
        editor.GoToLine(match.Line - 1);
        editor.SelectInLine(match.Line - 1, match.Column, match.Length);
        editor.FocusEditor();
        StatusBarMessage($"{Path.GetFileName(match.FilePath)} 行 {match.Line}");
    }

    /// <summary>目录替换写盘后，未修改过的同名标签页重读磁盘，避免屏幕上还是旧内容</summary>
    /// <param name="sender">目录查找面板的事件源，未使用</param>
    /// <param name="changedPaths">本次被写盘的目录替换受影响文件列表</param>
    private void OnDirReplaceCompleted(object? sender, IReadOnlyList<string> changedPaths)
    {
        var affected = changedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int reloaded = 0;
        // ToList() 快照：遍历期间 ReloadFromDiskQuietly 不会动集合，但保持防御性写法
        foreach (var doc in _documentItems.Keys.ToList())
        {
            if (doc.FilePath.Length == 0 || !affected.Contains(Path.GetFullPath(doc.FilePath))) continue;
            var editor = GetEditor(doc);
            if (editor == null) continue;
            if (doc.IsDirty) continue;   // 有未保存改动时不覆盖用户内容
            ReloadFromDiskQuietly(doc, editor);
            reloaded++;
        }
        if (reloaded > 0) StatusBarMessage($"目录替换完成，已刷新 {reloaded} 个打开的标签页");
    }

    /// <summary>替换后的重载不再逐个弹窗确认</summary>
    /// <param name="doc">需要重读磁盘的文档（同时更新其编码与快照）</param>
    /// <param name="editor">该文档对应的编辑器，用于保留光标/滚动位置并回填文本</param>
    private void ReloadFromDiskQuietly(Document doc, EditorControl editor)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
            // 按文档既有编码解码（不做重新检测）；同步磁盘快照，免得刚替换完又触发外部变更询问
            editor.LoadContent(EncodingService.Decode(bytes, doc.CodePage, stripBom: true));
            doc.IsDirty = false;
            doc.DiskTimestamp = File.GetLastWriteTime(doc.FilePath);
            doc.DiskLength = bytes.LongLength;
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
        }
        catch
        {
            // 磁盘读失败时保留屏幕内容即可
        }
    }

    /// <summary>文档 → 编辑器控件的两级反查（doc→item→editor），任何一环缺失返回 null</summary>
    /// <param name="doc">要反查的文档模型</param>
    /// <returns>该文档当前的编辑器控件；文档未挂在标签上时为 null</returns>
    private EditorControl? GetEditor(Document doc)
        => _documentItems.TryGetValue(doc, out var tab) && _itemEditors.TryGetValue(tab, out var editor) ? editor : null;

    // ==================== 文件对比 ====================

    /// <summary>文件对比窗口单例引用；窗口关闭时置回 null（ND2 用 QPointer 单例，同款语义）</summary>
    private CompareWindow? _compareWindow;

    /// <summary>拿到（或新建）对比窗口；已存在则激活置顶，保证同一时刻只有一个</summary>
    /// <returns>可直接 Show 的文件对比窗口实例</returns>
    private CompareWindow EnsureCompareWindow()
    {
        if (_compareWindow != null)
        {
            _compareWindow.Activate();
            return _compareWindow;
        }
        _compareWindow = new CompareWindow
        {
            Owner = this,
            // 对比窗口里双击文件名等操作回主窗口打开时走的回调
            OpenFileRequested = path => OpenFile(path),
        };
        _compareWindow.Closed += (_, _) => _compareWindow = null;
        _compareWindow.Show();
        return _compareWindow;
    }

    /// <summary>打开文件对比窗口（Ctrl+Shift+R），状态栏提示用法</summary>
    private void ShowCompare()
    {
        EnsureCompareWindow();
        StatusBarMessage("在对比窗口里选择左右两侧文件（F7 跳到下一个差异）");
    }

    /// <summary>菜单「对比文件」的 Click 转发</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnCompareFiles(object sender, RoutedEventArgs e) => ShowCompare();

    /// <summary>「与磁盘版本对比」：左侧是编辑区（可能含未保存改动），右侧是文件现状</summary>
    private void CompareWithDisk()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null || doc.FilePath.Length == 0)
        {
            StatusBarMessage("当前文档没有对应的磁盘文件，请先保存");
            return;
        }

        try
        {
            // 磁盘侧按文档编码解码并去 BOM，保证两侧文本可直接逐行对比
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
            string disk = EncodingService.Decode(bytes, doc.CodePage, stripBom: true);
            var win = EnsureCompareWindow();
            win.SetLeft(doc.FilePath, editor.GetContent());
            win.SetRight(doc.FilePath, disk);
            StatusBarMessage("左＝编辑区内容，右＝磁盘版本");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "对比失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>菜单「与磁盘版本对比」的 Click 转发</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnCompareWithDisk(object sender, RoutedEventArgs e) => CompareWithDisk();

    // ==================== 目录对比 ====================

    /// <summary>目录对比窗口单例引用</summary>
    private DirCompareWindow? _dirCompareWindow;

    /// <summary>拿到（或新建）目录对比窗口；它的「对比这两个文件」回调直接复用文件对比窗口</summary>
    /// <returns>可直接 Show 的目录对比窗口实例</returns>
    private DirCompareWindow EnsureDirCompareWindow()
    {
        if (_dirCompareWindow != null)
        {
            _dirCompareWindow.Activate();
            return _dirCompareWindow;
        }
        _dirCompareWindow = new DirCompareWindow
        {
            Owner = this,
            OpenFileRequested = path => OpenFile(path),
            // 结果表里选中一对差异文件时，送进文件对比窗口展示逐行 diff
            CompareFilesRequested = (left, right) =>
            {
                var win = EnsureCompareWindow();
                win.SetLeftFromFile(left);
                win.SetRightFromFile(right);
            },
        };
        _dirCompareWindow.Closed += (_, _) => _dirCompareWindow = null;
        return _dirCompareWindow;
    }

    /// <summary>菜单「目录对比」：首次打开时若当前有已保存文件，默认把它的目录填成一侧</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnDirCompare(object sender, RoutedEventArgs e)
    {
        var win = EnsureDirCompareWindow();
        if (!win.IsVisible && _currentDocument?.FilePath is { Length: > 0 } path)
            win.SetDirectories(Path.GetDirectoryName(path) ?? string.Empty);
        win.Show();
        win.Activate();
        StatusBarMessage("目录对比：左右各填一个目录（可粘贴路径），点「开始对比」");
    }

    // ==================== 外部对比工具 ====================

    /// <summary>调用外部 WinMerge 对比两个手选文件；找不到 WinMerge 时给出安装提示</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnCompareWithExternalTool(object sender, RoutedEventArgs e)
    {
        string tool = FindWinMerge();
        if (tool.Length == 0)
        {
            MessageBox.Show(this,
                "没有找到 WinMerge（WinMergeU.exe）。\n请先安装 WinMerge，或把它加入 PATH。",
                "外部对比工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 左文件默认带上当前文档路径，少点一次
        if (!PickFileWithDialog("选择左侧文件", _currentDocument?.FilePath, out var left)) return;
        if (!PickFileWithDialog("选择右侧文件", null, out var right)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool)
            {
                Arguments = $"\"{left}\" \"{right}\"",
                UseShellExecute = false,
            });
            StatusBarMessage($"已用 WinMerge 打开：{Path.GetFileName(left)} ↔ {Path.GetFileName(right)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>单文件选择对话框；initial 非空时预填文件名与目录。用户取消返回 false</summary>
    /// <param name="title">对话框标题（如「选择左侧文件」）</param>
    /// <param name="initial">预填的完整路径；可为 null 表示不预填</param>
    /// <param name="path">输出参数：用户确认后选中的绝对路径；取消时为不可靠值，调用方应先看返回值</param>
    /// <returns>true 表示用户选中了文件；false 表示取消</returns>
    private bool PickFileWithDialog(string title, string? initial, out string path)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = "所有文件 (*.*)|*.*" };
        if (initial is { Length: > 0 })
        {
            dlg.FileName = initial;
            dlg.InitialDirectory = Path.GetDirectoryName(initial);
        }
        path = dlg.ShowDialog(this) == true ? dlg.FileName : string.Empty;
        return path.Length > 0;
    }

    /// <summary>定位 WinMerge：先查两个标准安装目录，再逐段扫 PATH；都找不到返回空串</summary>
    /// <returns>WinMerge.exe 的完整路径；未安装时为 string.Empty</returns>
    private static string FindWinMerge()
    {
        // Program Files 与 Program Files (x86) 各试一次（32/64 位安装都覆盖）
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            string candidate = Path.Combine(root, "WinMerge", "WinMergeU.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // PATH 里逐段拼 WinMergeU.exe；空段/非法段直接跳过
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "WinMergeU.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH 里的非法路径跳过
            }
        }

        return string.Empty;
    }

    // ==================== 列编辑 ====================

    /// <summary>列编辑窗口单例引用（一个窗口绑定一个编辑器的当前列上下文）</summary>
    private ColumnEditWindow? _columnEditWindow;

    /// <summary>菜单「列编辑…」：为当前编辑器开列编辑窗口；已有窗口则激活</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnColumnEdit(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentEditor();
        if (editor == null)
        {
            StatusBarMessage("先打开一个文件再做列编辑");
            return;
        }
        if (_columnEditWindow != null)
        {
            _columnEditWindow.Activate();
            return;
        }
        _columnEditWindow = new ColumnEditWindow(editor) { Owner = this };
        _columnEditWindow.Closed += (_, _) => _columnEditWindow = null;
        _columnEditWindow.Show();
    }

    /// <summary>在当前选区各行行首插入「1. 2. 3.」序号；从末行往上插，避免先插入使后面行号漂移</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnInsertLineNumbers(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        // 取列编辑上下文（起止行等，后两项这里用不上）
        var (startLine, endLine, _, _) = editor.GetColumnEditContext();
        // 包成一个编辑组：撤销时一步整体回退，而不是逐行撤 N 次
        editor.BeginEditGroup();
        try
        {
            for (int line = endLine; line >= startLine; line--)
                editor.InsertColumnText(line, 0, (line - startLine + 1) + ". ");
        }
        finally
        {
            editor.EndEditGroup();
        }
        StatusBarMessage($"已在第 {startLine + 1}–{endLine + 1} 行行首插入序号");
    }

    // ==================== 哈希计算 ====================

    /// <summary>哈希窗口单例引用；内容以回调方式现取，避免窗口持有旧文本快照</summary>
    private HashWindow? _hashWindow;

    /// <summary>打开哈希计算窗口：传「取选区」「取全文」两个回调，窗口按需拉取最新内容</summary>
    private void ShowHash()
    {
        if (_hashWindow != null)
        {
            _hashWindow.Activate();
            return;
        }
        _hashWindow = new HashWindow(
            () => GetCurrentEditor()?.SelectedText ?? string.Empty,
            () => GetCurrentEditor()?.GetContent() ?? string.Empty)
        {
            Owner = this,
        };
        _hashWindow.Closed += (_, _) => _hashWindow = null;
        _hashWindow.Show();
        StatusBarMessage("哈希计算：选中文本 / 整个文档 / 剪贴板 / 文件");
    }

    /// <summary>菜单「哈希计算」的 Click 转发（Ctrl+Alt+H 也走 ShowHash）</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnShowHash(object sender, RoutedEventArgs e) => ShowHash();

    // ==================== 当前文件查找全部 / 批量查找 ====================

    /// <summary>结果列表对应的是哪个文档，跳转时即使已切标签也要回到它</summary>
    private Document? _searchHitsOwner;

    /// <summary>「查找所有打开文件」时逐条命中的归属文档；用引用比较，避免相同行列的两条命中互相覆盖</summary>
    private readonly Dictionary<SearchHit, Document> _searchHitOwners = new(ReferenceEqualityComparer.Instance);

    /// <summary>「在当前文件中查找全部」（多关键词）：把面板的查找词按行拆成关键词表查当前文件</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnFindAllInFile(object sender, RoutedEventArgs e)
    {
        CommitFindState();
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            return;
        }
        RunInFileSearch(new[] { _lastFind.Text }, _lastFind);
    }

    /// <summary>ND2「在当前文件中查找」：把查找框的条件当多关键词表来查当前文件</summary>
    /// <param name="sender">菜单项，转发给 OnFindAllInFile</param>
    /// <param name="e">RoutedEventArgs，原样转发</param>
    private void OnFindAllInCurrentDoc(object sender, RoutedEventArgs e) => OnFindAllInFile(sender, e);

    /// <summary>
    /// ND2「查找所有打开文件」：遍历所有标签页的缓冲区（含未保存内容），
    /// 结果按标签页分组并列在同一个面板里，双击跳回命中的那个标签页。
    /// </summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnFindAllInOpenedFiles(object sender, RoutedEventArgs e)
    {
        CommitFindState();
        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            return;
        }
        if (_documents.Count == 0)
        {
            StatusBarMessage("没有打开的文件");
            return;
        }

        // 每条命中记录归属文档（引用作键），跳回时才不会跳错标签
        _searchHitOwners.Clear();
        _searchHitsOwner = null;
        var all = new List<SearchHit>();
        int hitFiles = 0;
        int askTimes = 0;
        foreach (var doc in _documents)
        {
            var editor = GetEditor(doc);
            if (editor == null) continue;

            // 查缓冲区而非磁盘：未保存的改动也要参与（ND2 同款）
            var hits = InFileSearch.FindAll(editor.GetContent(), new[] { _lastFind }, sourceLabel: doc.DisplayName);
            if (hits.Count == 0) continue;
            hitFiles++;
            all.AddRange(hits);
            foreach (var hit in hits) _searchHitOwners[hit] = doc;

            // 硬上限 MaxHits 到了直接停；与 ND2 一样，总数跨过 2000 / 4000 各问一次
            if (all.Count >= InFileSearch.MaxHits) break;
            if ((askTimes == 0 && all.Count > InFileSearch.AskAtFirst)
                || (askTimes == 1 && all.Count > InFileSearch.AskAtSecond))
            {
                askTimes++;
                if (!AskContinue(hitFiles, all.Count)) break;
            }
        }

        SearchResultsPanel.ShowResults($"全部打开文件（{hitFiles}/{_documents.Count} 个文件命中）·「{_lastFind.Text}」", all);
        StatusBarMessage($"查找完成，一共 {all.Count} 处发现在 {hitFiles} 个文件！");
        // 结果面板负责展示，编辑器里的高亮撤掉免得屏幕花
        GetCurrentEditor()?.ClearFindHighlight();
    }

    /// <summary>菜单「批量查找」的 Click 转发</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnBatchFind(object sender, RoutedEventArgs e) => ShowBatchFind();

    /// <summary>
    /// ND2「提取正则结果到剪切板」(copyReResultBt)：只在正则模式可用，
    /// 一边把结果列进结果面板，一边把每处匹配到的文本逐行写进剪切板。
    /// </summary>
    /// <param name="sender">结果面板的事件源，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnCopyRegexResultRequested(object? sender, EventArgs e)
    {
        CommitFindState();
        var editor = GetCurrentEditor();
        if (editor == null) return;

        if (_lastFind.IsEmpty)
        {
            StatusBarMessage("查找字段为空");
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        // ND2 明确限定正则模式：非正则时状态栏原话劝退
        if (_lastFind.Mode != SearchMode.Regex)
        {
            StatusBarMessage("只在正则查找模式才能使用该功能！");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        RunInFileSearch(new[] { _lastFind.Text }, _lastFind);
        var texts = editor.CollectMatchTexts(_lastFind);
        if (texts.Count == 0)
        {
            StatusBarMessage($"没有找到 '{_lastFind.Text}'");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        try
        {
            // 每处匹配一行写进剪贴板
            System.Windows.Clipboard.SetText(string.Join("\n", texts));
            StatusBarMessage($"查找完成，一共 {texts.Count} 处发现，目标结果字段已经提取到剪切板。");
        }
        catch
        {
            StatusBarMessage("剪切板被其它程序占用，提取失败");
        }
    }

    /// <summary>Ctrl+Alt+F 的落点：等价于菜单「在当前文件中查找全部」</summary>
    private void RunFindAllShortcut() => OnFindAllInFile(this, new RoutedEventArgs());

    /// <summary>ND2 是同一个非模态窗口（QPointer 单例），可边切标签边点动作</summary>
    private BatchFindReplaceWindow? _batchFindWindow;

    /// <summary>打开/激活批量查找替换窗口；它拿「当前编辑器」与「轮色槽」两个回调与主窗口联动</summary>
    private void ShowBatchFind()
    {
        if (_batchFindWindow != null)
        {
            _batchFindWindow.Activate();
            return;
        }
        _batchFindWindow = new BatchFindReplaceWindow(GetCurrentEditor, FindReplacePanel.TakeMarkSlot) { Owner = this };
        _batchFindWindow.Closed += (_, _) => _batchFindWindow = null;
        _batchFindWindow.Show();
    }

    /// <summary>在当前文档跑多关键词查找并填充结果面板（各入口的公共尾部）</summary>
    /// <param name="terms">关键词列表（每行一个，已去掉空行）</param>
    /// <param name="proto">搜索条件原型：是否区分大小写/全词/正则等，逐词复用它</param>
    private void RunInFileSearch(IReadOnlyList<string> terms, SearchCondition proto)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        // 每个关键词共享同一套条件（大小写/正则等），只换 Text
        var conditions = terms.Select(t => proto with { Text = t }).ToList();
        var hits = InFileSearch.FindAll(editor.GetContent(), conditions, AskContinue, doc.DisplayName);
        _searchHitOwners.Clear();
        _searchHitsOwner = doc;
        SearchResultsPanel.ShowResults($"{doc.DisplayName}（{terms.Count} 个关键词）", hits);
        StatusBarMessage($"查找完成，一共 {hits.Count} 处发现。");

        // ND2 的多关键词查找只出结果列表；编辑器高亮只有一个 indicator，单关键词时才铺
        if (terms.Count == 1) editor.HighlightAllMatches(conditions[0]);
        else editor.ClearFindHighlight();
    }

    /// <summary>ND2 的「是否继续查找？」：命中数跨过阈值时询问，选否就停止收集</summary>
    /// <param name="hitFiles">当前已命中的文件数（用于文案）</param>
    /// <param name="matches">当前已命中的匹配总数（用于文案）</param>
    /// <returns>true 表示继续收集；false 表示到此为止</returns>
    private bool AskContinue(int hitFiles, int matches)
        => MessageBox.Show(this,
               $"查找结果已经有 {matches} 处在 {hitFiles} 个文件中，结果太多会比较慢，是否继续查找？",
               "是否继续查找？", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>结果面板双击一条命中：跳回归属文档并选中该处文本</summary>
    /// <param name="hit">被双击的命中记录（归属文档键 + 行/列位置 + 匹配文本）</param>
    private void OnSearchHitJumpRequested(SearchHit hit)
    {
        // 归属优先级：「查找所有打开文件」的逐条记录 → 单文件查找的整表记录 → 当前文档
        var doc = _searchHitOwners.TryGetValue(hit, out var owner) ? owner : _searchHitsOwner ?? _currentDocument;
        if (doc == null) return;
        if (!ReferenceEquals(doc, _currentDocument)) SelectDocument(doc);

        var editor = GetEditor(doc);
        if (editor == null) return;
        // 结果里是 1 基行/列，编辑器 API 是 0 基
        editor.GoToLine(hit.Line - 1);
        editor.SelectInLine(hit.Line - 1, hit.Column - 1, hit.Length);
        editor.FocusEditor();
        StatusBarMessage($"行 {hit.Line}，列 {hit.Column}：{hit.Term}");
    }

    /// <summary>结果面板关闭：清空列表与归属映射，顺带撤掉编辑器里的查找高亮</summary>
    private void OnSearchResultsCloseRequested()
    {
        SearchResultsPanel.ClearResults();
        _searchHitsOwner = null;
        _searchHitOwners.Clear();
        GetCurrentEditor()?.ClearFindHighlight();
    }

    /// <summary>查找下一个（F3）：用宿主上一次的 _lastFind/_wrapAround/_backwardSearch，找不到报状态栏</summary>
    private void FindNext()
    {
        var editor = GetCurrentEditor();
        if (editor == null || _lastFind.IsEmpty) return;
        if (!editor.FindNext(_lastFind, _wrapAround, _backwardSearch))
            StatusBarMessage($"没有找到「{_lastFind.Text}」");
    }

    /// <summary>查找上一个（Shift+F3 / F4）：强制反向，其余条件同 FindNext</summary>
    private void FindPrevious()
    {
        var editor = GetCurrentEditor();
        if (editor == null || _lastFind.IsEmpty) return;
        if (!editor.FindNext(_lastFind, _wrapAround, backward: true))
            StatusBarMessage($"没有找到「{_lastFind.Text}」");
    }

    // ==================== 跳转到行 ====================

    /// <summary>Ctrl+G：输入行号跳过去；行号非法时警告框提示</summary>
    private void GoToLine()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var input = Prompt.ShowInputDialog(this, "转到行", "请输入行号：");
        if (string.IsNullOrWhiteSpace(input)) return;

        if (int.TryParse(input.Trim(), out int lineNo))
        {
            editor.GoToLine(lineNo - 1);
        }
        else
        {
            MessageBox.Show(this, "请输入有效的行号。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== 书签 ====================

    /// <summary>在当前行切换书签（Ctrl+F2），并刷新书签面板</summary>
    private void ToggleBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ToggleBookmark(editor.CurrentLine);
        UpdateBookmarkBar(editor);
    }

    /// <summary>跳到下一个书签（F2）；没有书签时不动</summary>
    private void NextBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        var line = editor.NextBookmark(editor.CurrentLine);
        if (line != null) editor.GoToLine(line.Value);
    }

    /// <summary>跳到上一个书签（Shift+F2）</summary>
    private void PreviousBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        var line = editor.PreviousBookmark(editor.CurrentLine);
        if (line != null) editor.GoToLine(line.Value);
    }

    // ND2 把这对按钮做成「hex 翻页 / 文本位置回退」两用，按当前标签的文档类型分支（ccnotepad.cpp:7969-8040）
    /// <summary>位置回退（ND2 prePosBtn：文本态跳上一处编辑位置；hex 态本应翻页，未实现）</summary>
    private void GotoPrePos()
    {
        if (_currentDocument?.IsHexView == true)
        {
            StatusBarMessage("十六进制翻页还没接（现在的 hex 是整篇渲染，没有分页）");
            return;
        }
        var editor = GetCurrentEditor();
        if (editor == null) return;
        // 空栈 = 蜂鸣 + 状态栏提示（ccnotepad.cpp:7975-7978）
        if (!editor.GotoPrePos())
        {
            System.Media.SystemSounds.Beep.Play();
            StatusBarMessage("没有前一个位置了");
        }
    }

    /// <summary>位置前进：与 GotoPrePos 成对的双栈另一侧</summary>
    private void GotoNextPos()
    {
        if (_currentDocument?.IsHexView == true)
        {
            StatusBarMessage("十六进制翻页还没接（现在的 hex 是整篇渲染，没有分页）");
            return;
        }
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!editor.GotoNextPos())
        {
            System.Media.SystemSounds.Beep.Play();
            StatusBarMessage("没有后一个位置了");
        }
    }

    /// <summary>清除当前文档全部书签并刷新书签面板</summary>
    private void ClearBookmarks()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ClearBookmarks();
        UpdateBookmarkBar(editor);
    }

    /// <summary>把编辑器的书签行列表刷进右侧书签面板；面板隐藏时直接跳过（省一次全量重建）</summary>
    /// <param name="editor">取书签行的编辑器；为 null 或 hex 视图时清空面板</param>
    private void UpdateBookmarkBar(EditorControl editor)
    {
        if (BookmarkBarPanel.Visibility != Visibility.Visible) return;

        var bookmarks = editor.GetBookmarks();
        BookmarkItems.ItemsSource = bookmarks
            .Select(l => new BookmarkItem(l, $"行 {l + 1}"))
            .ToList();
    }

    /// <summary>点书签面板某条：跳到那一行并清空选中（允许连续点同一行）</summary>
    /// <param name="sender">书签列表控件，其 SelectedItem 即被点的 BookmarkItem</param>
    /// <param name="e">选中变化参数，未使用（只在新选中项非 null 时动作）</param>
    private void OnBookmarkItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BookmarkItems.SelectedItem is BookmarkItem item)
        {
            GetCurrentEditor()?.GoToLine(item.Line);
            BookmarkItems.SelectedItem = null;
        }
    }

    /// <summary>书签面板的数据项：行号 + 显示文本「行 N」</summary>
    private class BookmarkItem
    {
        /// <summary>0 基行号（跳转直接用）</summary>
        public int Line { get; }
        /// <summary>面板上显示的文字</summary>
        public string Text { get; }
        /// <summary>构造一条书签展示项</summary>
        /// <param name="line">0 基行号（跳行直接用，不需要再减一）</param>
        /// <param name="text">面板显示文字（由调用方拼成「行 N」）</param>
        public BookmarkItem(int line, string text) { Line = line; Text = text; }
    }

    // ==================== 主题 ====================

    /// <summary>亮/暗互换（Ctrl+T）并即时刷新整个 UI</summary>
    private void ToggleTheme()
    {
        ThemeService.ToggleTheme();
        ApplyThemeToUI();
    }

    /// <summary>换主题字典并让所有编辑器重取颜色；DynamicResource 会自动传播到可视树</summary>
    private void ApplyThemeToUI()
    {
        bool dark = ThemeService.CurrentTheme == ThemeService.ThemeMode.Dark;

        // 目标主题字典（打包在组件资源里，用 pack 相对 Uri 载入）
        var newDict = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "/NotepadSharp;component/Resources/ThemeDark.xaml"
                     : "/NotepadSharp;component/Resources/ThemeLight.xaml",
                UriKind.Relative)
        };

        var appRes = Application.Current.Resources;
        var merged = appRes.MergedDictionaries;

        // 找到现有主题字典并整体替换（替换才能触发 DynamicResource 刷新）
        ResourceDictionary? existing = null;
        foreach (var d in merged)
        {
            var src = d.Source?.OriginalString ?? string.Empty;
            if (src.Contains("ThemeLight") || src.Contains("ThemeDark"))
            {
                existing = d;
                break;
            }
        }

        if (existing != null)
            merged[merged.IndexOf(existing)] = newDict;
        else
            merged.Add(newDict);

        // 停靠主题的颜色全部走 DynamicResource，换主题时自动跟随

        // 应用到所有编辑器
        foreach (var editor in _itemEditors.Values)
        {
            editor.ApplyTheme();
        }
    }

    // ==================== 显示选项 / 缩放 ====================

    /// <summary>自动换行开关：以当前编辑器为准取反，然后广播到所有已打开文档并落盘</summary>
    private void ToggleWordWrap()
    {
        bool v = !(GetCurrentEditor()?.WordWrap ?? false);
        foreach (var e in _itemEditors.Values) e.WordWrap = v;
        MenuWordWrap.IsChecked = v;
        SettingsService.Current.WordWrap = v;
    }

    /// <summary>
    /// ND2 changeBlankShowStatus（ccnotepad.cpp:6733-6756）：状态没变就什么都不做，
    /// 变了就对所有编辑器一起下发、落盘，最后把三个菜单项的状态同步回来。
    /// </summary>
    /// <param name="blankShow">ND2 的位域值：1=显示空格/TAB，2=显示行尾，3=全部显示，0=全关</param>
    private void ChangeBlankShowStatus(int blankShow)
    {
        // 值没变直接 return（连盘都不落），避免无谓的全量刷新
        if ((GetCurrentEditor()?.BlankShow ?? SettingsService.Current.BlankShow) == blankShow) return;

        // 对所有已打开编辑器统一下发（ND2 同款：这是全局显示选项，不是每文档选项）
        foreach (var e in _itemEditors.Values) e.BlankShow = blankShow;
        SettingsService.Current.BlankShow = blankShow;
        SyncBlankShowStatus();
    }

    /// <summary>ND2 syncBlankShowStatus（:6722-6730）：三个菜单项按位域回填，显示所有 = 3</summary>
    private void SyncBlankShowStatus()
    {
        int blankShow = GetCurrentEditor()?.BlankShow ?? SettingsService.Current.BlankShow;
        MenuShowSpaces.IsChecked = (blankShow & 1) != 0;
        MenuShowLineEnd.IsChecked = (blankShow & 2) != 0;
        MenuShowAllWhite.IsChecked = blankShow == 3;
    }

    /// <summary>只控制空格和 TAB，不控制行尾（ND2 slot_show_spaces :6768-6780）</summary>
    private void ToggleSpaces()
    {
        int blankShow = GetCurrentEditor()?.BlankShow ?? SettingsService.Current.BlankShow;
        ChangeBlankShowStatus((blankShow & 1) != 0 ? blankShow & 0xe : blankShow | 0x1);
    }

    /// <summary>只控制行尾（ND2 slot_show_line_end :7744-7756）</summary>
    private void ToggleLineEnd()
    {
        int blankShow = GetCurrentEditor()?.BlankShow ?? SettingsService.Current.BlankShow;
        ChangeBlankShowStatus((blankShow & 2) != 0 ? blankShow & 0xd : blankShow | 0x2);
    }

    /// <summary>工具栏那个空白按钮 / 菜单「显示所有」：全开或全关（ND2 slot_allWhite :6760-6765）</summary>
    private void ToggleAllWhite()
    {
        int blankShow = GetCurrentEditor()?.BlankShow ?? SettingsService.Current.BlankShow;
        ChangeBlankShowStatus(blankShow == 3 ? 0 : 3);
    }

    /// <summary>放大一档字号并刷新状态栏缩放率显示</summary>
    private void ZoomIn()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ZoomIn();
        UpdateStatusBar(e);
    }

    /// <summary>缩小一档字号并刷新状态栏</summary>
    private void ZoomOut()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ZoomOut();
        UpdateStatusBar(e);
    }

    /// <summary>字号复位到 100% 并刷新状态栏</summary>
    private void ZoomReset()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ResetZoom();
        UpdateStatusBar(e);
    }

    /// <summary>缩进参考线（ND2 slot_indentGuide :6784-6801）。ND2 只对文本页下发，hex 页不动；这里同样跳过 hex。</summary>
    private void ToggleIndentGuide()
    {
        bool v = !(GetCurrentEditor()?.ShowIndentGuides ?? SettingsService.Current.ShowIndentGuide);
        // 遍历「文档+标签」对：需要看文档类型（hex 跳过）所以不能只遍历编辑器
        foreach (var (doc, item) in _documentItems)
        {
            if (doc.IsHexView) continue;
            if (_itemEditors.TryGetValue(item, out var e)) e.ShowIndentGuides = v;
        }
        MenuIndentGuide.IsChecked = v;
        SettingsService.Current.ShowIndentGuide = v;
    }

    /// <summary>显示工具栏（ND2 slot_showToolBar :9873-9878：只切可见性 + 落盘）</summary>
    private void ToggleToolBar()
    {
        bool v = MainToolBar.Visibility != Visibility.Visible;
        MainToolBar.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        MenuShowToolBar.IsChecked = v;
        SettingsService.Current.ShowToolBar = v;
    }

    /// <summary>
    /// 显示网站（ND2 slot_showWebAddr :9880-9885）：只改进程级静态位 + 落盘，
    /// 不回头刷新已经开着的编辑器 —— 关掉以后旧的热点残影留着，是 ND2 的原有表现。
    /// </summary>
    private void ToggleWebAddr()
    {
        bool v = !EditorControl.HighlightWebAddr;
        EditorControl.HighlightWebAddr = v;
        MenuShowWebAddr.IsChecked = v;
        SettingsService.Current.ShowWebAddr = v;
    }

    /// <summary>切到指定主题（菜单「亮色/暗色」直选，与 ToggleTheme 的互换相对）</summary>
    /// <param name="mode">目标主题枚举（Light/Dark），同时写入设置</param>
    private void SetTheme(ThemeService.ThemeMode mode)
    {
        ThemeService.SetTheme(mode);
        ApplyThemeToUI();
    }

    /// <summary>在资源管理器里打开设置文件所在目录（目录不存在先创建）</summary>
    private void OpenSettingsFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsService.SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开设置目录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// ND2 的 Tab 设置在「选项 → 文本和字体」页里（texteditsetwin），改完在关页时落到所有已打开编辑器
    /// （ccnotepad.cpp:8541 slot_tabFormatChange），这里照抄：关窗才生效 + 遍历所有标签页
    /// </summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnTabSettings(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;
        var win = new Views.TabSettingsWindow(s.TabWidth, s.ReplaceTabWithSpace, (len, replace) =>
        {
            s.TabWidth = len;
            s.ReplaceTabWithSpace = replace;
            foreach (var editor in _itemEditors.Values) editor.ApplyTabSettings();
            // ND2 退出程序时才写回 tablens/tabnouse，这里顺手落盘，避免崩了丢设置
            SettingsService.Save();
        })
        {
            Owner = this,
        };
        win.Show();
    }

    // ==================== 保存/关闭 ====================
    /// <summary>保存全部：逐个把 _currentDocument 指过去复用 SaveFile（自动处理无路径→另存为），结束后切回原活跃文档</summary>
    private void SaveAll()
    {
        // SaveFile 只认 _currentDocument，所以批量保存要临时接管它；先存好原值
        var original = _currentDocument;
        foreach (var doc in _documents.ToList())
        {
            _currentDocument = doc;
            if (doc.IsDirty || string.IsNullOrEmpty(doc.FilePath))
                SaveFile();
        }
        _currentDocument = original;
        if (original != null) SelectDocument(original);
    }

    /// <summary>关闭当前活动标签（Ctrl+W）</summary>
    private void CloseCurrentFile()
    {
        var item = GetActiveItem();
        if (item != null) CloseTab(item);
    }

    /// <summary>关闭全部标签：每个都独立走保存/锁定确认，任一被取消则那一个留下</summary>
    private void CloseAllFiles()
    {
        // ToList() 快照：CloseTab 会就地修改 _documents
        foreach (var doc in _documents.ToList())
            CloseTab(_documentItems[doc]);
    }

    /// <summary>关闭除当前活动标签外的所有标签</summary>
    private void CloseOtherFiles()
    {
        var keep = GetActiveItem();
        foreach (var doc in _documents.ToList())
        {
            var item = _documentItems[doc];
            if (!ReferenceEquals(item, keep)) CloseTab(item);
        }
    }

    /// <summary>关闭当前标签左侧的所有标签：按所在标签组里的实际顺序判定左右</summary>
    private void CloseLeftFiles()
    {
        var active = GetActiveItem();
        if (active == null || !_itemViews.TryGetValue(active, out var activeView)) return;
        if (activeView.Parent is not DocumentGroup group) return;

        // 从活动项往前逐个关（倒序关闭，避免中途索引漂移）
        var siblings = group.Children.OfType<View>().ToList();
        int idx = siblings.IndexOf(activeView);
        if (idx <= 0) return;
        for (int i = idx - 1; i >= 0; i--)
        {
            if (_viewItems.TryGetValue(siblings[i], out var item)) CloseTab(item);
        }
    }

    /// <summary>关闭当前标签右侧的所有标签</summary>
    private void CloseRightFiles()
    {
        var active = GetActiveItem();
        if (active == null || !_itemViews.TryGetValue(active, out var activeView)) return;
        if (activeView.Parent is not DocumentGroup group) return;

        // 从尾部往前关，同样为了保持未处理项的索引稳定
        var siblings = group.Children.OfType<View>().ToList();
        int idx = siblings.IndexOf(activeView);
        if (idx < 0) return;
        for (int i = siblings.Count - 1; i > idx; i--)
        {
            if (_viewItems.TryGetValue(siblings[i], out var item)) CloseTab(item);
        }
    }

    /// <summary>锁定当前标签：防误关，也锁住编码切换</summary>
    private void ToggleTabLock()
    {
        var doc = _currentDocument;
        if (doc == null) return;
        doc.IsLocked = !doc.IsLocked;
        UpdateTabHeader(doc);
        StatusBarMessage(doc.IsLocked ? $"已锁定标签：{doc.DisplayName}" : $"已解锁标签：{doc.DisplayName}");
    }

    /// <summary>置顶开关（Ctrl+Alt+P）：改 Topmost 属性即可，其余窗口由 OnTopmostChanged 联动</summary>
    private void ToggleTopmost() => Topmost = !Topmost;

    /// <summary>置顶要盖到程序的每一个窗口：VS 停靠的浮动窗格、目录对比等是独立顶层窗口，不跟主窗口的 Z 序带</summary>
    /// <param name="sender">「总在最前」菜单项，其 IsChecked 即新的置顶状态</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        // 遍历应用全部活窗口统一同步 Topmost（主窗口自己已由属性切换完成）
        foreach (Window w in Application.Current.Windows)
            if (!ReferenceEquals(w, this)) w.Topmost = Topmost;

        SettingsService.Current.AlwaysOnTop = Topmost;
        SettingsService.Save();
        if (_readyForStatusMessages)
            StatusBarMessage(Topmost ? "已固定置顶：本程序在所有窗口最前" : "已取消固定置顶");
    }

    /// <summary>重新加载当前文件：无路径/文件没了先拦住，有未保存改动要确认，最后走 ReloadFromDisk</summary>
    private void ReloadFile()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        if (string.IsNullOrEmpty(doc.FilePath))
        {
            MessageBox.Show(this, "当前文件尚未保存到磁盘，无法重新加载。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!File.Exists(doc.FilePath))
        {
            MessageBox.Show(this, "文件不存在：" + doc.FilePath, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (doc.IsDirty)
        {
            var r = MessageBox.Show(this,
                $"\"{doc.DisplayName}\" 有未保存的改动，重新加载会丢弃这些改动。是否继续？",
                "重新加载", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        ReloadFromDisk(doc, editor);
    }

    /// <summary>从磁盘按文档当前编码重读内容（「重新加载文件」与外部变更提示共用）</summary>
    /// <param name="doc">要重读的文档（提供路径与编码；重读后更新其快照与脏标记）</param>
    /// <param name="editor">目标编辑器，重读期间保留光标与滚动位置</param>
    private void ReloadFromDisk(Document doc, EditorControl editor)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
            editor.LoadContent(EncodingService.Decode(bytes, doc.CodePage, stripBom: true));
            // 重载即「与磁盘对齐」：清脏标记并刷新磁盘快照，外部监控不会再报
            doc.IsDirty = false;
            doc.DiskTimestamp = File.GetLastWriteTime(doc.FilePath);
            doc.DiskLength = bytes.LongLength;
            editor.SetLexerForFile(doc.FilePath);
            editor.ApplyTheme();
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
            StatusBarMessage($"已重新载入 {doc.DisplayName}（{EncodingService.GetDisplayName(doc.CodePage, doc.HasBom)}）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重新加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>在资源管理器打开当前文件所在目录；无文件时退回文件树根目录</summary>
    private void OpenCurrentFileFolder()
    {
        var doc = _currentDocument;
        string dir;
        if (doc != null && !string.IsNullOrEmpty(doc.FilePath))
            dir = Path.GetDirectoryName(doc.FilePath) ?? _fileTreeRoot;
        else
            dir = _fileTreeRoot;

        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show(this, "没有可用的目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开文件夹失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>在光标处插入当前时间（yyyy-MM-dd HH:mm:ss），走 ReplaceSelection 可撤销</summary>
    private void InsertDateTime()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.Scintilla.ReplaceSelection(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>字数统计对话框：行数/字符数/非空白字符数/单词数（中文按字计，英文按词计）</summary>
    private void ShowStats()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var content = editor.GetContent();
        var lines = editor.LineCount;
        var chars = editor.TextLength;
        var nonWhitespace = content.Count(c => !char.IsWhiteSpace(c));
        // 正则口径：每个汉字算一个「词」，连续的字母数字下划线算一个
        var words = Regex.Matches(content, @"[一-龥]|[A-Za-z0-9_]+").Count;

        MessageBox.Show(this,
            $"行数：{lines}\n字符数：{chars}\n不含空白字符数：{nonWhitespace}\n单词数：{words}",
            "字数统计", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>在「当前文件所在目录」开一个交互式 cmd（/K 保持不关窗）</summary>
    private void OpenCmdInCurrentDir()
    {
        var doc = _currentDocument;
        var dir = (doc != null && !string.IsNullOrEmpty(doc.FilePath))
            ? Path.GetDirectoryName(doc.FilePath) ?? _fileTreeRoot
            : _fileTreeRoot;

        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /K 后带 cd /d：跨盘符也能切过去，窗口保持打开
                Arguments = "/K \"cd /d \"" + dir + "\"\"",
                WorkingDirectory = dir,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开 CMD 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>「标记所有行」：给每一行加书签（后续配合删除标记行/反选做全文行级筛选）</summary>
    private void MarkAllLines()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        for (int i = 0; i < editor.LineCount; i++)
        {
            editor.AddBookmark(i);
        }
        UpdateBookmarkBar(editor);
    }

    // ==================== JSON 格式化 / 压缩 ====================

    /// <summary>JSON 格式化选项：缩进输出 + 不转义非 ASCII（中文原样保留，与 ND2 观感一致）</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonIndentOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON 压缩选项：单行输出 + 同上宽松编码</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonCompactOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>格式化 JSON（Ctrl+Shift+F）：作用范围由 TryGetWorkRange 定（选区优先）</summary>
    private void FormatJson() => ProcessJson(JsonIndentOptions, "格式化 JSON");

    /// <summary>压缩 JSON（Ctrl+Shift+M）</summary>
    private void MinifyJson() => ProcessJson(JsonCompactOptions, "压缩 JSON");

    /// <summary>JSON 处理公共流程：取范围 → Parse 校验 → 按选项重序列化 → 原位替换选区/全文</summary>
    /// <param name="options">序列化选项（缩进版 JsonIndentOptions 或压缩版 JsonCompactOptions）</param>
    /// <param name="action">提示文案里的动作名（如「格式化」「压缩」），失败时进消息框</param>
    private void ProcessJson(System.Text.Json.JsonSerializerOptions options, string action)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!TryGetWorkRange(editor, out string content, out int start, out int end, action)) return;

        try
        {
            // 先 Parse 一遍当语法校验，非法 JSON 在下面 catch 里报行号级错误
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var text = System.Text.Json.JsonSerializer.Serialize(doc.RootElement, options);

            // 用「设选区 + ReplaceSelection」写回：整步可撤销，且不破坏撤销栈里其它操作
            editor.Scintilla.SetSelection(start, end);
            editor.Scintilla.ReplaceSelection(text);

            MessageBox.Show(this, action + "完成。", action, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Text.Json.JsonException ex)
        {
            MessageBox.Show(this, "JSON 解析失败：" + ex.Message + "\n\n请确认内容是合法的 JSON。",
                action + "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, action + "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>有单行选区时只处理选区，否则处理整个文档</summary>
    /// <param name="editor">当前编辑器（提供选区与全文）</param>
    /// <param name="content">输出参数：本次要处理的文本（选区文本或全文）</param>
    /// <param name="start">输出参数：替换区间起点（文档绝对偏移）</param>
    /// <param name="end">输出参数：替换区间终点（文档绝对偏移）</param>
    /// <param name="action">动作名；范围为空/无文档时用于弹提示</param>
    /// <returns>true 表示拿到了可处理的文本与区间；false 表示已提示并应中止</returns>
    private bool TryGetWorkRange(Controls.EditorControl editor, out string content, out int start, out int end, string action)
    {
        var sel = editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n'))
        {
            content = sel;
            start = editor.Scintilla.SelectionStart;
            end = editor.Scintilla.SelectionEnd;
        }
        else
        {
            content = editor.GetContent();
            start = 0;
            end = editor.Scintilla.TextLength;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show(this, "没有可处理的内容。", action, MessageBoxButton.OK, MessageBoxImage.Information);
            start = end = 0;
            return false;
        }
        return true;
    }

    // ==================== XML 格式化 / 压缩 ====================

    /// <summary>格式化 XML：两空格缩进重建</summary>
    private void FormatXml() => ProcessXml(false, "格式化 XML");

    /// <summary>压缩 XML：去缩进单行输出</summary>
    private void MinifyXml() => ProcessXml(true, "压缩 XML");

    /// <summary>XML 处理公共流程：Parse → XmlWriter 序列化 → 手工回填 XML 声明 → 原位替换</summary>
    /// <param name="compact">true 表示压缩（不写缩进/空白），false 表示两空格缩进格式化</param>
    /// <param name="action">提示文案里的动作名（「格式化 XML」/「压缩 XML」）</param>
    private void ProcessXml(bool compact, string action)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!TryGetWorkRange(editor, out string content, out int start, out int end, action)) return;

        try
        {
            // XDocument.Parse 默认丢弃 insignificant whitespace，所以压缩后不会再带原来的缩进
            var doc = System.Xml.Linq.XDocument.Parse(content);
            var sb = new System.Text.StringBuilder();
            // 用 XmlWriter 才能控制声明里的编码字样：写进 StringWriter 会被标成 utf-16
            var writerSettings = new System.Xml.XmlWriterSettings
            {
                Indent = !compact,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = System.Xml.NewLineHandling.Replace,
                // 声明由下面按原文回填，省得 StringWriter 写出 utf-16
                OmitXmlDeclaration = true,
            };
            using (var xmlWriter = System.Xml.XmlWriter.Create(new System.IO.StringWriter(sb), writerSettings))
                doc.Save(xmlWriter);
            string text = sb.ToString().TrimEnd('\r', '\n');
            // 声明优先用解析出来的；XDocument 没带声明但原文有（如注释掉/异常场景）就抄原文第一行
            if (doc.Declaration is { } decl) text = decl.ToString() + "\n" + text;
            else if (content.TrimStart().StartsWith("<?xml")) text = FirstLine(content.TrimStart()) + "\n" + text;

            editor.Scintilla.SetSelection(start, end);
            editor.Scintilla.ReplaceSelection(text);

            MessageBox.Show(this, action + "完成。", action, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Xml.XmlException ex)
        {
            // XmlException 自带出错行号，直接报给用户定位
            MessageBox.Show(this, "XML 解析失败（第 " + ex.LineNumber + " 行）：" + ex.Message,
                action + "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, action + "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>取文本第一行（去尾部 \r），用于回填原文的 XML 声明行</summary>
    /// <param name="text">原始 XML 全文</param>
    /// <returns>第一行内容；没有换行符时即整段文本</returns>
    private static string FirstLine(string text)
    {
        int idx = text.IndexOf('\n');
        return idx < 0 ? text : text[..idx].TrimEnd('\r');
    }

    // ==================== Markdown / 文本 转 HTML ====================

    /// <summary>把当前文档转成 HTML 放进新标签：Markdown 走渲染器，纯文本转义后包 pre</summary>
    private void ConvertToHtml()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var content = editor.GetContent();
        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show(this, "没有内容可转换。", "转 HTML", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 按扩展名判断是否 Markdown；未保存文档没有扩展名，按纯文本处理
        var ext = _currentDocument != null
            ? Path.GetExtension(_currentDocument.FilePath).ToLowerInvariant()
            : string.Empty;
        bool isMarkdown = ext is ".md" or ".markdown";

        try
        {
            string html;
            if (isMarkdown)
            {
                // Markdown → HTML
                html = MarkdownService.RenderToHtml(content);
            }
            else
            {
                // 纯文本 → HTML（转义 + <pre> 保留换行）
                var escaped = System.Net.WebUtility.HtmlEncode(content);
                html = "<pre>\n" + escaped.Replace("\r\n", "\n").Replace("\r", "\n") + "\n</pre>";
            }

            // 在新标签页展示转换结果
            var doc = new Document
            {
                FilePath = string.Empty,
                DisplayName = "转换结果.html",
                CodePage = EncodingService.CpUtf8,
                HasBom = false,
            };
            var tab = AddDocument(doc);
            var ed = _itemEditors[tab];
            ed.LoadContent(html);
            // 借一个假的 .html 文件名让词法器按 HTML 上色
            ed.SetLexerForFile("converted.html");
            ed.ApplyTheme();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "转 HTML 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 菜单构建 ====================

    /// <summary>代码生成「编码」下的两个子菜单：转换编码（改保存编码）与重新载入（按指定编码重读）</summary>
    private void BuildEncodingMenu()
    {
        // 「转换编码」列表来自 EncodingService.ConvertOptions
        ConvertEncodingMenu.Items.Clear();
        foreach (var opt in EncodingService.ConvertOptions)
        {
            var item = new MenuItem { Header = opt.Display };
            var captured = opt;
            item.Click += (_, _) => ConvertEncoding(captured);
            ConvertEncodingMenu.Items.Add(item);
        }

        // 「以指定编码载入」列表来自 ReloadOptions
        ReloadEncodingMenu.Items.Clear();
        foreach (var opt in EncodingService.ReloadOptions)
        {
            var item = new MenuItem { Header = opt.Display };
            var captured = opt;
            item.Click += (_, _) => ReloadEncoding(captured);
            ReloadEncodingMenu.Items.Add(item);
        }
    }

    /// <summary>转换编码：只改「保存时用哪种编码」，屏幕内容不动；锁定标签与 hex 视图拒绝</summary>
    /// <param name="option">菜单 Tag 携带的目标编码描述（代码页 + 是否 BOM）</param>
    private void ConvertEncoding(EncodingOption option)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        // 锁定标签禁改编码（ND2 tabLock 同时锁编码切换）
        if (doc.IsLocked)
        {
            StatusBarMessage("标签已锁定，先按 Ctrl+Alt+T 解锁才能切换编码");
            return;
        }

        if (doc.IsHexView)
        {
            MessageBox.Show(this, "十六进制视图没有编码概念。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 转换编码只改「保存时用哪种编码」，屏幕内容不动，因此文档变为待保存状态
        doc.CodePage = option.CodePage;
        doc.HasBom = option.Bom;
        doc.IsDirty = true;
        UpdateTabHeader(doc);
        UpdateStatusBar(editor);
        StatusBarMessage($"已转为 {option.Display}（保存后写入磁盘）");
    }

    /// <summary>以指定编码重新载入当前文件：有未保存改动要先确认丢弃，然后带 reloadEncoding 走 OpenFile</summary>
    /// <param name="option">菜单 Tag 携带的重新载入编码描述</param>
    private void ReloadEncoding(EncodingOption option)
    {
        var doc = _currentDocument;
        if (doc == null) return;

        if (string.IsNullOrEmpty(doc.FilePath))
        {
            MessageBox.Show(this, "当前文件尚未保存到磁盘，无法按指定编码重新载入。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (doc.IsDirty)
        {
            var r = MessageBox.Show(this,
                $"按 {option.Display} 重新载入会丢弃未保存的改动，是否继续？",
                "重新载入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        OpenFile(doc.FilePath, (option.CodePage, option.Bom));
    }

    /// <summary>切换保存时是否写 BOM；当前编码本身没有 BOM 概念（如 GBK）时提示后不动作</summary>
    /// <param name="sender">「设 BOM」菜单项，其 IsChecked 即新状态</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnToggleBom(object sender, RoutedEventArgs e)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        // GetPreamble 为空 = 该编码不带 BOM（ND2 同款拦截）
        if (EncodingService.GetPreamble(doc.CodePage).Length == 0)
        {
            MessageBox.Show(this, $"{EncodingService.GetDisplayName(doc.CodePage, false)} 不使用 BOM。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        doc.HasBom = !doc.HasBom;
        doc.IsDirty = true;
        UpdateTabHeader(doc);
        UpdateStatusBar(editor);
        StatusBarMessage(doc.HasBom ? "保存时将写入 BOM" : "保存时不写 BOM");
    }

    /// <summary>菜单「只读」的 Click 转发（与 Ctrl+Alt+R 同一开关）</summary>
    /// <param name="sender">菜单项，未使用（状态由 ToggleReadOnly 内部取反）</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnToggleReadOnly(object sender, RoutedEventArgs e) => ToggleReadOnly();

    /// <summary>「以十六进制重新打开」：静默关旧标签，再按字节转储开新标签</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnReopenAsHex(object sender, RoutedEventArgs e)
    {
        var doc = _currentDocument;
        if (doc == null || string.IsNullOrEmpty(doc.FilePath) || doc.IsHexView) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
            if (GetTab(doc) is { } tab) CloseTabSilently(tab);
            OpenHexView(doc.FilePath, bytes);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "十六进制打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>「以文本重新打开」：从 hex 回到正常打开流程，编码重新自动检测</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnReopenAsText(object sender, RoutedEventArgs e)
    {
        var doc = _currentDocument;
        if (doc == null || string.IsNullOrEmpty(doc.FilePath)) return;

        if (!doc.IsHexView)
        {
            MessageBox.Show(this, "当前已经是文本视图。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CloseTabSilently(GetTab(doc)!);
        var (codePage, bom) = EncodingService.Detect(File.ReadAllBytes(doc.FilePath));
        OpenFile(doc.FilePath, (codePage, bom));
    }

    /// <summary>编辑器里尝试输入但文档只读时的提示（任何标签的编辑器都会送过来）</summary>
    /// <param name="sender">触发提示的编辑器控件，用于定位其文档并给状态栏</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnReadOnlyEditAttempted(object? sender, EventArgs e)
    {
        StatusBarMessage("文档是只读的（Ctrl+Alt+R 可取消只读）");
    }

    // 括号里第三个值是给词法器注入关键字用的「样例扩展名」：Lexilla 没有独立的 JS/Go/TOML 词法器，
    // 这些语言借用近似词法器（cpp/props），关键字表必须按语言注入，否则关键字不上色
    /// <summary>「语言」菜单的数据表：显示名 / Lexilla 词法器名 / 关键字注入用的样例扩展名</summary>
    private static readonly (string Display, string Lexer, string? KeywordHint)[] Languages =
    {
        ("C / C++", "cpp", ".cpp"), ("C#", "cpp", ".cs"), ("Java", "cpp", ".java"),
        ("Python", "python", ".py"), ("HTML", "hypertext", ".html"), ("XML", "xml", ".xml"),
        ("CSS", "css", ".css"), ("JavaScript", "cpp", ".js"), ("TypeScript", "cpp", ".ts"),
        ("JSON", "json", ".json"), ("SQL", "sql", ".sql"), ("Markdown", "markdown", ".md"),
        ("YAML", "yaml", ".yaml"), ("TOML", "props", ".toml"), ("Bash", "bash", ".sh"),
        ("Batch", "batch", ".bat"), ("PowerShell", "powershell", ".ps1"), ("Perl", "perl", ".pl"),
        ("Lua", "lua", ".lua"), ("Ruby", "ruby", ".rb"), ("Go", "cpp", ".go"), ("Rust", "rust", ".rs"),
        ("Swift", "cpp", ".swift"), ("Kotlin", "cpp", ".kt"), ("Verilog", "verilog", ".v"),
        ("VHDL", "vhdl", ".vhd"), ("Assembly", "asm", ".asm"), ("Fortran", "fortran", ".f"),
        ("Pascal", "pascal", ".pas"), ("Properties", "props", ".ini"), ("Diff", "diff", ".diff"),
        ("纯文本", "null", null),
    };

    /// <summary>代码生成「语言」菜单：一项一语言，点击给当前编辑器重挂词法器并同步状态栏语法显示</summary>
    private void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        foreach (var (display, lexer, hint) in Languages)
        {
            var item = new MenuItem { Header = display };
            item.Click += (_, _) =>
            {
                var editor = GetCurrentEditor();
                if (editor == null) return;
                editor.SetLexerByName(lexer, hint);
                // 手动切换要覆盖「按扩展名反推」的显示值（状态栏读 LanguageDisplay）
                editor.LanguageDisplay = lexer == "null" ? "Text" : display;
                UpdateStatusBar(editor);
            };
            LanguageMenu.Items.Add(item);
        }
    }

    // ==================== 视图切换 ====================

    /// <summary>文件树面板开关：第一次打开且还没加载过时补一次 LoadFileTree</summary>
    private void ToggleFileTree()
    {
        FileTreePanel.Visibility = FileTreePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        MenuFileListView.IsChecked = FileTreePanel.Visibility == Visibility.Visible;

        if (FileTreePanel.Visibility == Visibility.Visible && FileTree.Items.Count == 0)
        {
            LoadFileTree();
        }
    }

    /// <summary>Markdown 预览面板开关：打开时立刻渲染一次当前文档</summary>
    private void ToggleMarkdown()
    {
        MarkdownPanel.Visibility = MarkdownPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

        if (MarkdownPanel.Visibility == Visibility.Visible)
            UpdateMarkdownPreview();
    }

    /// <summary>书签面板开关：打开时用当前编辑器重建书签列表</summary>
    private void ToggleBookmarkBar()
    {
        BookmarkBarPanel.Visibility = BookmarkBarPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

        if (BookmarkBarPanel.Visibility == Visibility.Visible)
        {
            var editor = GetCurrentEditor();
            if (editor != null) UpdateBookmarkBar(editor);
        }
    }

    /// <summary>渲染 Markdown 纯文本预览（RenderToPlainText：排版成等宽可读形态，不是 HTML）</summary>
    private void UpdateMarkdownPreview()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var content = editor.GetContent();
        MarkdownText.Text = MarkdownService.RenderToPlainText(content);
    }

    // ==================== 文件树 ====================

    /// <summary>文件树当前根目录；「打开所在目录/打开 CMD」在没有文件时退回用它</summary>
    private string _fileTreeRoot = string.Empty;

    /// <summary>加载文件树：根目录取当前文件所在目录，没有文档就取「我的文档」</summary>
    private void LoadFileTree()
    {
        string dir;
        if (_currentDocument != null && !string.IsNullOrEmpty(_currentDocument.FilePath))
        {
            dir = Path.GetDirectoryName(_currentDocument.FilePath)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        else
        {
            dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        SetFileTreeRoot(dir);
    }

    /// <summary>把文件树切到指定根目录并整树重建（目录不存在时静默忽略）</summary>
    /// <param name="dir">新的根目录绝对路径（拖文件夹进窗口、菜单「以此目录为根」都走这里）</param>
    private void SetFileTreeRoot(string dir)
    {
        if (!Directory.Exists(dir)) return;
        _fileTreeRoot = dir;

        FileTree.Items.Clear();
        var root = BuildDirectoryNode(dir);
        if (root != null)
        {
            FileTree.Items.Add(root);
        }
    }

    /// <summary>当前文档换了目录时让文件树跟随；面板隐藏或无文件时不动</summary>
    private void RefreshFileTreeForCurrentFile()
    {
        if (FileTreePanel.Visibility != Visibility.Visible) return;
        if (_currentDocument == null || string.IsNullOrEmpty(_currentDocument.FilePath)) return;

        // 只在根目录真的变了时重建整树（重建成本高，且会收起展开状态）
        var dir = Path.GetDirectoryName(_currentDocument.FilePath);
        if (!string.IsNullOrEmpty(dir) && dir != _fileTreeRoot)
        {
            SetFileTreeRoot(dir);
        }
    }

    // ==================== 文件树右键菜单 ====================

    /// <summary>代码构建文件树右键菜单：打开/新窗口打开、复制绝对/相对路径、新建、重命名、删除</summary>
    private void BuildFileTreeContextMenu()
    {
        var menu = new ContextMenu();

        // 打开族：普通打开 / 再开一个 MainWindow 实例打开
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => OnOpenTreeNode(false);
        menu.Items.Add(open);

        var openWin = new MenuItem { Header = "在新窗口打开" };
        openWin.Click += (_, _) => OnOpenTreeNode(true);
        menu.Items.Add(openWin);

        menu.Items.Add(new Separator());

        // 复制路径族
        var copyAbs = new MenuItem { Header = "复制绝对路径" };
        copyAbs.Click += (_, _) => CopyToClipboard(GetSelectedNodePath() ?? string.Empty);
        menu.Items.Add(copyAbs);

        var copyRel = new MenuItem { Header = "复制相对路径" };
        copyRel.Click += (_, _) =>
        {
            // 相对根目录截前缀；忽略大小写比较（Windows 路径），截完去掉开头的分隔符
            var node = FileTree.SelectedItem as FileNode;
            if (node == null || _fileTreeRoot.Length == 0) return;
            var rel = node.FullPath;
            if (rel.StartsWith(_fileTreeRoot, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(_fileTreeRoot.Length).TrimStart('\\');
            CopyToClipboard(rel);
        };
        menu.Items.Add(copyRel);

        menu.Items.Add(new Separator());

        // 新建族：带扩展名默认按文件建，不带按文件夹建（见 CreateInSelectedFolder）
        var newFile = new MenuItem { Header = "新建文件" };
        newFile.Click += (_, _) => CreateInSelectedFolder("新建文件.txt");
        menu.Items.Add(newFile);

        var newFolder = new MenuItem { Header = "新建文件夹" };
        newFolder.Click += (_, _) => CreateInSelectedFolder("新建文件夹");
        menu.Items.Add(newFolder);

        menu.Items.Add(new Separator());

        // 重命名 / 删除（都作用于当前选中节点）
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => OnRenameTreeNode();
        menu.Items.Add(rename);

        var del = new MenuItem { Header = "删除" };
        del.Click += (_, _) => OnDeleteTreeNode();
        menu.Items.Add(del);

        FileTree.ContextMenu = menu;
    }

    /// <summary>取选中节点完整路径；没选中返回 null</summary>
    /// <returns>选中 FileNode 的绝对路径；树里没有选中项时为 null</returns>
    private string? GetSelectedNodePath()
    {
        return (FileTree.SelectedItem as FileNode)?.FullPath;
    }

    /// <summary>取「新建」的目标目录：选中目录用它，选中文件用它所在目录，没选中用根</summary>
    /// <returns>可写文件的目录绝对路径（根也没设过时退回当前目录）</returns>
    private string GetSelectedNodeDir()
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null) return _fileTreeRoot;
        return node.IsDirectory ? node.FullPath : (Path.GetDirectoryName(node.FullPath) ?? _fileTreeRoot);
    }

    /// <summary>打开选中文件节点；newWindow=true 时新建一个 MainWindow 独立进程窗口去开</summary>
    /// <param name="newWindow">true 表示在新窗口打开（右键「新窗口打开」），false 表示当前窗口开标签</param>
    private void OnOpenTreeNode(bool newWindow)
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null || node.IsDirectory) return;

        if (newWindow)
        {
            // 新窗口自己会 NewFile()，随后立刻替换成目标文件（会短暂闪一下空标签，可接受）
            var win = new MainWindow();
            win.Show();
            win.OpenFile(node.FullPath);
        }
        else
        {
            OpenFile(node.FullPath);
        }
    }

    /// <summary>复制文本到剪贴板：WPF 失败退 WinForms，再失败把内容显示出来让用户手动复制</summary>
    /// <param name="text">要放进剪贴板的文本（绝对路径/相对路径等）</param>
    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 剪贴板偶尔被其它程序占用/Shell 属性系统异常，做多重尝试
        bool ok = false;
        try { System.Windows.Clipboard.SetText(text); ok = true; } catch { }
        if (!ok)
        {
            try { System.Windows.Forms.Clipboard.SetText(text); ok = true; } catch { }
        }

        if (ok)
        {
            MessageBox.Show(this, "已复制：" + text, "复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            // 回退：把路径显示出来，方便手动复制
            MessageBox.Show(this, "剪贴板暂时不可用，请手动复制以下路径：\n\n" + text,
                "复制", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>在选中目录新建文件/文件夹：名称含扩展名按文件建，否则按文件夹建；同名冲突拒绝</summary>
    /// <param name="defaultName">输入框预填名（「新建文件」预填无扩展名，「新建文件夹」预填「新建文件夹」）</param>
    private void CreateInSelectedFolder(string defaultName)
    {
        var dir = GetSelectedNodeDir();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法创建目录", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var input = Prompt.ShowInputDialog(this, "新建", "请输入名称（将创建到：" + dir + "）", defaultName);
        if (string.IsNullOrWhiteSpace(input)) return;

        var name = input.Trim();
        var full = Path.Combine(dir, name);
        try
        {
            if (File.Exists(full) || Directory.Exists(full))
            {
                MessageBox.Show(this, "同名文件或文件夹已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 判据是「有没有扩展名」：无扩展名视为文件夹
            if (Path.GetExtension(name).Length > 0)
                File.WriteAllText(full, string.Empty);
            else
                Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>重命名选中节点（文件/目录皆可），成功后整树重载</summary>
    private void OnRenameTreeNode()
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null) return;

        var input = Prompt.ShowInputDialog(this, "重命名", "新名称：", node.Name);
        if (string.IsNullOrWhiteSpace(input)) return;

        var newName = input.Trim();
        var newPath = Path.Combine(Path.GetDirectoryName(node.FullPath) ?? ".", newName);
        // 改名前后相同（忽略大小写）就当没发生
        if (string.Equals(newPath, node.FullPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            if (node.IsDirectory) Directory.Move(node.FullPath, newPath);
            else File.Move(node.FullPath, newPath);
            LoadFileTree();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重命名失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>删除选中节点：目录连内容递归删，删除前弹确认框</summary>
    private void OnDeleteTreeNode()
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null) return;

        var result = MessageBox.Show(this,
            $"确定要删除 \"{node.Name}\" 吗？此操作不可恢复。", "删除确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (node.IsDirectory) Directory.Delete(node.FullPath, true);
            else File.Delete(node.FullPath);
            LoadFileTree();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>递归构建一个目录节点：子目录在前文件在后；过滤噪音目录与非文本文件；无权限目录吞掉</summary>
    /// <param name="path">要展开的目录绝对路径</param>
    /// <returns>建好的目录 FileNode；目录已不可访问（被删/无权限）时返回 null</returns>
    private FileNode? BuildDirectoryNode(string path)
    {
        if (!Directory.Exists(path))
            return null;

        // 盘符根目录 GetFileName 为空，退回用完整路径当显示名
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            name = path;

        var node = new FileNode(path, name, true);

        try
        {
            // 目录过滤：点开头的隐藏目录、以及 node_modules/bin/obj 这类构建噪音
            var dirs = Directory.GetDirectories(path)
                .Where(d =>
                {
                    var dn = Path.GetFileName(d);
                    return !dn.StartsWith('.') && dn is not ("node_modules" or "bin" or "obj" or "__pycache__" or "Debug" or "Release");
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                var child = BuildDirectoryNode(dir);
                if (child != null) node.Children.Add(child);
            }

            // 文件过滤：二进制/媒体/字体等扩展名不进树（编辑不了也没意义）
            var files = Directory.GetFiles(path)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is not (".exe" or ".dll" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico"
                        or ".pdf" or ".zip" or ".rar" or ".7z" or ".gz" or ".tar" or ".bin" or ".dat" or ".lnk"
                        or ".mp3" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".ttf" or ".woff" or ".woff2");
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                node.Children.Add(new FileNode(file, Path.GetFileName(file), false));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限的目录忽略
        }
        catch (IOException)
        {
        }

        return node;
    }

    /// <summary>双击文件节点直接打开（目录节点双击是 TreeView 自带的展开/收起）</summary>
    /// <param name="sender">文件树 TreeView，OriginalSource 用于判断点中了什么</param>
    /// <param name="e">鼠标事件参数；只处理左键双击</param>
    private void OnFileTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode node && !node.IsDirectory)
        {
            OpenFile(node.FullPath);
        }
    }

    /// <summary>右侧最近文件列表选中一条即打开，并立刻清空选中以便再次点同一条</summary>
    /// <param name="sender">最近文件 ListBox，其 SelectedItem 是被点的 RecentFileItem</param>
    /// <param name="e">选中变化参数；新增选中项为空时不处理</param>
    private void OnRecentFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentFilesList.SelectedItem is string path)
        {
            RecentFilesList.SelectedItem = null;
            OpenFile(path);
        }
    }

    /// <summary>最近文件列表（最多 20 条，新的在前）；同时喂给右侧面板与「文件→最近文件」菜单</summary>
    private readonly List<string> _recentFiles = new();

    /// <summary>把一个路径挪到最近文件头部；超限截尾，并刷新面板与菜单两处展示</summary>
    /// <param name="filePath">刚成功打开/保存的文件绝对路径；目录或空串忽略</param>
    private void AddRecentFile(string filePath)
    {
        _recentFiles.Remove(filePath);
        _recentFiles.Insert(0, filePath);
        if (_recentFiles.Count > 20)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);

        // ListBox 对 List 源不会自动感知 Reset，先置 null 再重设强制重建
        RecentFilesList.ItemsSource = null;
        RecentFilesList.ItemsSource = _recentFiles;
        RefreshRecentFilesMenu();
    }

    // ==================== 状态栏 ====================

    /// <summary>把某编辑器的光标/选区/统计/编码/语法/脏态等刷进底部状态栏（各事件源的公共出口）</summary>
    /// <param name="editor">要读取状态的编辑器；为 null 时按「无文档」清空各栏</param>
    private void UpdateStatusBar(EditorControl editor)
    {
        // 载入/重载入时非当前标签的编辑器也会触发事件，若不拦截会把别的文档的统计写进状态栏
        if (!ReferenceEquals(editor, GetCurrentEditor())) return;

        // 左半边：位置与统计类（行列显示 1 基，给用户看的）
        StatusLineCol.Text = $"行: {editor.CurrentLine + 1} 列: {editor.CurrentColumn + 1}";
        StatusSel.Text = $"选中: {editor.SelectionLength}";
        StatusChars.Text = $"字符: {editor.TextLength}";
        StatusLines.Text = $"行数: {editor.LineCount}";
        StatusZoom.Text = $"缩放率: {100 + editor.ZoomLevel * 10}%";
        StatusEol.Text = editor.EolName;

        if (_currentDocument != null)
        {
            StatusEncoding.Text = EncodingService.GetDisplayName(_currentDocument.CodePage, _currentDocument.HasBom);

            // 语法栏跟着编辑器当前词法器走（ND2 syncCurDocLexerToMenu），不按文件名反推，
            // 否则手动切的语法会被每次选区刷新冲掉
            StatusLang.Text = $"语法: {editor.LanguageDisplay.ToLowerInvariant()}";
            StatusModified.Text = _currentDocument.IsDirty ? "●" : "";
            StatusReadonly.Text = _currentDocument.IsReadOnly
                ? (_currentDocument.IsHexView ? "十六进制" : "只读")
                : string.Empty;
            // 菜单勾选/表头文案与模型同步（BOM 菜单项做成动词短语随状态翻转）
            MenuReadOnly.IsChecked = _currentDocument.IsReadOnly;
            MenuBom.Header = _currentDocument.HasBom ? "去除 BOM" : "增加 BOM";
            if (_menuColumnMode != null) _menuColumnMode.IsChecked = editor.ColumnMode;
        }

        // Markdown 文件实时预览
        if (MarkdownPanel.Visibility == Visibility.Visible && _currentDocument != null)
        {
            var ext = Path.GetExtension(_currentDocument.FilePath).ToLowerInvariant();
            if (ext is ".md" or ".markdown")
            {
                // 渲染丢到 Background 优先级：别卡在每次选区变化的同步路径里
                Dispatcher.BeginInvoke(new Action(UpdateMarkdownPreview), DispatcherPriority.Background);
            }
        }
    }

    /// <summary>脏点/锁定图标变了以后刷新对应标签头（模板里绑的是 Document，触发 RefreshHeader 重渲染）</summary>
    /// <param name="doc">状态发生变化的文档；没有对应标签时什么都不做</param>
    private void UpdateTabHeader(Document doc)
    {
        if (_documentItems.TryGetValue(doc, out var item))
            item.RefreshHeader();
    }

    /// <summary>标签头按钮区（锁定/关闭）的左键按下必须吞掉，不让拖拽逻辑接管</summary>
    /// <param name="sender">按钮所在的标签头元素，未使用</param>
    /// <param name="e">鼠标按下参数；命中左键时置 Handled</param>
    private void OnTabHeaderButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 不拦截的话，VS 的 DragUndockHeader 会把点击按钮当成拖拽起点
        if (e.ChangedButton == MouseButton.Left) e.Handled = true;
    }

    /// <summary>标签头小锁按钮：先选中该标签再切换锁定态（按钮可能在非活动标签上）</summary>
    /// <param name="sender">锁按钮，其 DataContext 是所属 Document</param>
    /// <param name="e">鼠标抬起参数；置 Handled 防止冒泡成拖拽起点</param>
    private void OnTabLockPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Button.Tag 由模板绑成所属布局项，借此找回文档
        if (e.ChangedButton != MouseButton.Left || sender is not Button { Tag: DocumentLayoutItem item }) return;
        e.Handled = true;
        SelectDocument(item.Document);
        ToggleTabLock();
    }

    /// <summary>标签头 ✕ 按钮：走完整 CloseTab 流程（含保存确认与锁定检查）</summary>
    /// <param name="sender">关闭按钮，其 DataContext 是所属 Document</param>
    /// <param name="e">鼠标抬起参数；置 Handled 防止冒泡成拖拽起点</param>
    private void OnTabClosePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Button { Tag: DocumentLayoutItem item }) return;
        e.Handled = true;
        CloseTab(item);
    }

    // ==================== 停靠事件 ====================

    /// <summary>VS 报告活动 View 变了：更新当前文档、状态栏、书签面板；拖拽期间把焦点/文件树推迟到松手</summary>
    /// <param name="sender">ViewManager，未使用</param>
    /// <param name="e">携带新活动 View 的事件参数；为 null 表示没有活动视图</param>
    private void OnVsActiveViewChanged(object? sender, ActiveViewChangedEventArgs e)
    {
        var view = ViewManager.Instance.ActiveView;
        if (view == null || !_viewItems.TryGetValue(view, out var item)) return;

        _currentDocument = item.Document;
        if (_itemEditors.TryGetValue(item, out var editor))
        {
            UpdateStatusBar(editor);
            UpdateBookmarkBar(editor);
            if (_headerDragActive)
            {
                // 拖拽中不做抢焦点/切文件树（会让停靠反馈窗闪烁），记下等 FlushDeferredActivation 补
                _pendingFileTreeRefresh = true;
                _pendingFocusEditor = editor;
            }
            else
            {
                RefreshFileTreeForCurrentFile();
                editor.FocusEditor();
            }
        }
        EmptyStateText.Visibility = _documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>浮动窗口标题栏的 ✕：统一走保存确认 + 锁定检查</summary>
    /// <param name="sender">命令源，未使用</param>
    /// <param name="e">CanExecute 参数：置 CanExecute 决定 ✕ 按钮是否可点</param>
    private void OnCanCloseFloatingViewCommand(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = e.Parameter is ViewElement;

    /// <summary>执行浮动窗关闭命令：目标是单个 View 就关一个，是整个停靠容器就递归关掉里面所有文档 View</summary>
    /// <param name="sender">命令源，未使用</param>
    /// <param name="e">Executed 参数，其 Source 是要关闭的视图/容器</param>
    private void OnCloseFloatingViewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ViewElement element) return;
        // 关掉整个浮动窗时里面可能有多个标签，FindAll 全捞出来逐个走 CloseTab（保留各自的确认流程）
        var views = element is View single ? new List<View> { single } : element.FindAll<View>().ToList();
        foreach (var view in views)
        {
            if (_viewItems.TryGetValue(view, out var item)) CloseTab(item);
        }
    }

    /// <summary>视图被隐藏时走正常关闭流程；取消关闭则恢复显示</summary>
    /// <param name="sender">IsVisible 变 false 的 View，其 Content 是 DocumentLayoutItem</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnViewIsVisibleChanged(object? sender, EventArgs e)
    {
        // 只处理「从可见变不可见」；_closingFromVisibility 挡住我们自己触发的隐藏再入
        if (sender is not View view || view.IsVisible || _closingFromVisibility) return;
        if (!_viewItems.TryGetValue(view, out var item)) return;

        // BeginInvoke 排到 Background：等 VS 停靠布局的内部状态收敛后再关，避免在事件处理器里改树
        _closingFromVisibility = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _closingFromVisibility = false;
            if (!_itemViews.ContainsKey(item)) return;
            CloseTab(item);
            // 用户在确认框里点了取消 → 把 View 重新显示回去
            if (_itemViews.ContainsKey(item) && !view.IsVisible)
                view.IsVisible = true;
        }), DispatcherPriority.Background);
    }

    /// <summary>标签右键菜单（DragUndockHeader 只发事件，不会自己弹菜单）</summary>
    /// <param name="sender">标签头 DragUndockHeader，其 DataContext 是右键命中的 View</param>
    /// <param name="e">VS 提供的上下文菜单位置；弹出时要按屏幕坐标摆放</param>
    private void OnTabHeaderContextMenu(object? sender, DragUndockHeaderContextMenuEventArgs e)
    {
        // 右键来源必须是挂在某个 View 上的标签头，且该 View 在我们的登记表里
        if (e.OriginalSource is not DragUndockHeader header || header.ViewElement is not View view) return;
        if (!_viewItems.TryGetValue(view, out var item)) return;

        var menu = new ContextMenu
        {
            PlacementTarget = header,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        // 局部函数：往菜单塞一条「文字 + 动作」项
        /// <summary>往右键菜单追加一条「文字 + 动作」的菜单项</summary>
        /// <param name="text">菜单文案</param>
        /// <param name="action">点击后执行的动作（闭包捕获右键命中的 item）</param>
        void Add(string text, Action action)
        {
            var entry = new MenuItem { Header = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        // 关闭族：单个 / 其他 / 全部
        Add("关闭", () => CloseTab(item));
        Add("关闭其他标签页", () =>
        {
            foreach (var doc in _documents.ToList())
            {
                if (!ReferenceEquals(doc, item.Document)) CloseTab(_documentItems[doc]);
            }
        });
        Add("关闭全部标签页", CloseAllFiles);
        menu.Items.Add(new Separator());
        // 布局族：浮动/停靠/拆组（右键菜单点谁就作用于谁，不受当前活动标签限制）
        Add("浮动为独立窗口", () => FloatItem(item));
        Add("停靠回主窗口", () => DockItem(item));
        Add("向右新建标签组", () => NewTabGroup(item, toRight: true));
        Add("向下新建标签组", () => NewTabGroup(item, toRight: false));

        menu.IsOpen = true;
        e.Handled = true;   // 告诉停靠库菜单已弹出，别再走默认处理
    }

    /// <summary>菜单：把当前标签拖出主窗口，变成可自由移动的浮动窗口</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnFloatCurrentTab(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) FloatItem(item);
    }

    /// <summary>浮动指定标签：已在浮动窗里就只切换过去，否则交给 DockOperations.Float 拆出</summary>
    /// <param name="item">要浮动出去的标签布局项（右键菜单传入，菜单项不依赖当前活动标签）</param>
    private void FloatItem(DocumentLayoutItem item)
    {
        if (!_itemViews.TryGetValue(item, out var view)) return;
        if (FloatSite.IsFloating(view))
        {
            SelectDocument(item.Document);
            return;
        }

        DockOperations.Float(view, _windowProfile!);
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        StatusBarMessage($"“{item.Document.DisplayName}”已浮动为独立窗口，拖回标签栏即可重新停靠");
    }

    /// <summary>菜单：把浮动的标签停靠回主窗口</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnDockCurrentTab(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) DockItem(item);
    }

    /// <summary>停靠指定标签回主窗口的标签组（主组拿不到时退回主标签组）</summary>
    /// <param name="item">要收回主窗口的标签布局项；本就没在浮动窗里时只提示不动作</param>
    private void DockItem(DocumentLayoutItem item)
    {
        if (!_itemViews.TryGetValue(item, out var view)) return;
        if (!FloatSite.IsFloating(view))
        {
            StatusBarMessage("当前标签已经在主窗口内");
            return;
        }

        var target = DockOperations.GetMainSiteDocumentGroup(_windowProfile!)
            ?? DockOperations.GetPrimaryDocumentGroup(_windowProfile!);
        DockOperations.Dock(target, view, DockDirection.Fill);
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        StatusBarMessage($"“{item.Document.DisplayName}”已停靠回主窗口");
    }

    /// <summary>菜单「新建垂直标签组」：把当前标签拆到右侧新组（左右并排比对用）</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnNewVerticalTabGroup(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) NewTabGroup(item, toRight: true);
    }

    /// <summary>菜单「新建水平标签组」：把当前标签拆到下方新组</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnNewHorizontalTabGroup(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) NewTabGroup(item, toRight: false);
    }

    /// <summary>把指定标签拆到新的标签组：toRight=true 左右并排，false 上下排列</summary>
    /// <param name="item">要拆出去的标签布局项</param>
    /// <param name="toRight">true 表示新组放在右侧（垂直分栏），false 表示放在下方（水平分栏）</param>
    private void NewTabGroup(DocumentLayoutItem item, bool toRight)
    {
        if (!_itemViews.TryGetValue(item, out var view)) return;
        if (FloatSite.IsFloating(view))
        {
            StatusBarMessage("浮动窗口里的标签先停靠回主窗口再拆分组");
            return;
        }
        if (view.Parent is not DocumentGroup group)
        {
            StatusBarMessage("当前标签不在文档标签组里");
            return;
        }
        // 组里只有一个标签时拆组没有意义（拆完原组就空了），拒绝并提示
        if (group.Children.Count <= 1)
        {
            StatusBarMessage("当前标签组里只有这一个标签，先把别的标签拖进来再拆");
            return;
        }

        // 建组方向：Horizontal = 新组在右侧并排，Vertical = 新组在下方
        var newGroup = DockOperations.CreateDocumentGroup(group,
            toRight ? Orientation.Horizontal : Orientation.Vertical);
        DockOperations.Dock(newGroup, view, DockDirection.Fill);
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        StatusBarMessage(toRight ? "已向右新建标签组" : "已向下新建标签组");
    }

    /// <summary>视图菜单：所有文档回到主标签组，关闭浮动窗口，回收空标签组</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnResetDockLayout(object sender, RoutedEventArgs e)
    {
        if (_windowProfile == null) return;
        var main = DockOperations.GetMainSiteDocumentGroup(_windowProfile)
            ?? DockOperations.GetPrimaryDocumentGroup(_windowProfile);
        if (main == null) return;

        // 第一遍先把浮动窗里的 View 全部拖回主组（浮动窗失去内容会自动关闭）
        foreach (var view in _windowProfile.FindAll<View>().ToList())
        {
            if (FloatSite.IsFloating(view))
                DockOperations.Dock(main, view, DockDirection.Fill);
        }

        // 第二遍再把主窗口里散落的其它标签组并入主组（FindAll 快照后再动树，避免边遍历边改）
        foreach (var view in _windowProfile.FindAll<View>().ToList())
        {
            if (view.Parent is DocumentGroup group && !ReferenceEquals(group, main))
                DockOperations.Dock(main, view, DockDirection.Fill);
        }

        if (_currentDocument != null) SelectDocument(_currentDocument);
        StatusBarMessage("已把全部标签停靠回主窗口");
    }

    // ==================== 关闭 ====================

    /// <summary>关窗前的未保存拦截：列出所有脏文档，Yes=逐个保存后退出，Cancel=留在界面，No=丢弃直接关</summary>
    /// <param name="sender">主窗口自身，作为消息框的所有者</param>
    /// <param name="e">可取消事件参数：需要留下时置 Cancel = true</param>
    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        var modified = _documents.Where(d => d.IsDirty).ToList();
        if (modified.Count == 0) return;

        var names = string.Join("\n", modified.Select(d => "• " + d.DisplayName));
        var result = MessageBox.Show(this,
            $"以下文件尚未保存：\n{names}\n\n是否保存后退出？",
            "未保存的文件", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (result == MessageBoxResult.Yes)
        {
            // 临时接管 _currentDocument 逐个 SaveFile；无路径的会弹另存为，用户取消则保持脏
            foreach (var doc in modified)
            {
                _currentDocument = doc;
                SaveFile();
            }
            // 只要还有一个没保存成功就不退出，让用户回去处理
            if (_documents.Any(d => d.IsDirty))
                e.Cancel = true;
        }
    }

    /// <summary>主窗口已关闭：静默收尾所有浮动窗（不再走确认流程），最后落盘设置</summary>
    /// <param name="sender">主窗口自身，未使用</param>
    /// <param name="e">标准 EventArgs，未使用</param>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 主窗口关闭前已经处理过未保存提示，浮动窗收尾时不再走“视图隐藏→关文档”的确认流程
        _closingFromVisibility = true;
        foreach (var window in ViewManager.Instance.FloatingWindowManager.FloatingWindows.ToList())
        {
            try { window.Close(); } catch { }
        }
        SaveSettings();
    }

    /// <summary>把窗口几何、主题、面板可见性、最近文件、打开中的文件写回 SettingsService 并落盘</summary>
    private void SaveSettings()
    {
        var s = SettingsService.Current;
        s.Theme = ThemeService.CurrentTheme == ThemeService.ThemeMode.Dark ? "Dark" : "Light";

        // 最大化状态下 Width/Left 是虚值，只记 Maximized 标志，不覆盖正常几何
        if (WindowState == WindowState.Maximized)
        {
            s.Maximized = true;
        }
        else
        {
            s.Maximized = false;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }

        s.ShowFileTree = FileTreePanel.Visibility == Visibility.Visible;
        s.ShowMarkdown = MarkdownPanel.Visibility == Visibility.Visible;
        s.ShowBookmarkBar = BookmarkBarPanel.Visibility == Visibility.Visible;
        s.RecentFiles = _recentFiles.ToList();
        // 会话恢复只记有磁盘路径的文档；未命名草稿不跨会话（避免恢复出一堆空标签）
        s.OpenFiles = _documents
            .Where(d => !string.IsNullOrEmpty(d.FilePath))
            .Select(d => d.FilePath)
            .ToList();

        SettingsService.Save();
    }

    // ==================== 自动更新 ====================

    /// <summary>本次运行是否已做过启动静默检查（防止窗口重建等场景重复查）</summary>
    private bool _startupUpdateChecked;

    /// <summary>启动后静默查一次：没更新、断网都不出声，只有确实有新版本才打扰</summary>
    private void ScheduleStartupUpdateCheck()
    {
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        // 延迟 5 秒再发请求 + ApplicationIdle 优先级：把启动窗口期让给文件恢复与界面渲染
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(5000);
            await Services.UpdateService.CheckForUpdatesAsync(this, showUpToDateDialog: false, StatusBarMessage);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>菜单「检查更新」：手动触发，已是最新也弹框告知</summary>
    /// <param name="sender">菜单项，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await Services.UpdateService.CheckForUpdatesAsync(this, showUpToDateDialog: true, StatusBarMessage);
    }

    // ==================== 关于 ====================

    /// <summary>「关于」对话框：版本号来自 UpdateService.CurrentVersion（单一事实源）</summary>
    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"NotepadSharp v{Services.UpdateService.CurrentVersion}\n\n" +
            "基于 WPF + Scintilla.NET 的轻量级文本编辑器\n\n" +
            "功能亮点：\n" +
            "• 多标签页编辑\n" +
            "• 语法高亮（30+ 种语言）\n" +
            "• 查找/替换（支持正则）\n" +
            "• 书签管理\n" +
            "• 代码折叠\n" +
            "• Markdown 实时预览\n" +
            "• 亮色/暗色主题\n" +
            "• 文件树浏览\n" +
            "• 编码自动识别（UTF-8 / GBK / UTF-16）\n\n" +
            "开源仓库：https://github.com/LuoCoreOpenCode/NotepadSharp",
            "关于 NotepadSharp", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== RelayCommand ====================

    /// <summary>
    /// 极简 ICommand：命令永可执行（CanExecute 恒真），只包一个无参 Action。
    /// 本窗口所有命令的可用性判定都放在动作内部（GetCurrentEditor() 为 null 就静默跳过），
    /// 因此不需要 InvalidationRequery 联动，菜单项也就不置灰而是点了没反应——与 ND2 的观感一致。
    /// </summary>
    private class RelayCommand : ICommand
    {
        /// <summary>要执行的动作</summary>
        private readonly Action _execute;

        /// <summary>用无参动作构造命令</summary>
        /// <param name="execute">命令要执行的无参动作（通常是 () => Xxx() 形式的方法引用）</param>
        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        /// <summary>可执行性变化事件：恒可执行所以无人订阅，add/remove 留空实现防 null 引用</summary>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        /// <summary>恒真：命令永远可用，实际保护逻辑在各动作里</summary>
        /// <param name="parameter">绑定传入的参数，未使用</param>
        /// <returns>恒为 true</returns>
        public bool CanExecute(object? parameter) => true;

        /// <summary>执行命令：忽略 parameter 直接跑动作</summary>
        /// <param name="parameter">绑定传入的参数，未使用</param>
        public void Execute(object? parameter) => _execute();
    }
}
