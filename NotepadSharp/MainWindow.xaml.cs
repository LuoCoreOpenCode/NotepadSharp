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

public partial class MainWindow : Window
{
    private List<Document> _documents { get; } = new();
    private Dictionary<Document, DocumentLayoutItem> _documentItems = new();
    private Dictionary<DocumentLayoutItem, EditorControl> _itemEditors = new();
    private Dictionary<DocumentLayoutItem, View> _itemViews = new();
    private Dictionary<View, DocumentLayoutItem> _viewItems = new();
    private WindowProfile? _windowProfile;
    private Document? _currentDocument;
    private bool _closingFromVisibility;
    private int _newFileCounter;
    private bool _readyForStatusMessages;

    // 查找替换状态
    private string _findText = string.Empty;
    private string _replaceText = string.Empty;
    private bool _matchCase;
    private bool _matchWord;
    private bool _useRegex;

    // 命令
    public ICommand NewFileCommand { get; private set; } = null!;
    public ICommand OpenFileCommand { get; private set; } = null!;
    public ICommand SaveFileCommand { get; private set; } = null!;
    public ICommand SaveAsCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand CutCommand { get; private set; } = null!;
    public ICommand CopyCommand { get; private set; } = null!;
    public ICommand PasteCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand ToggleCommentCommand { get; private set; } = null!;
    public ICommand UpperCaseCommand { get; private set; } = null!;
    public ICommand LowerCaseCommand { get; private set; } = null!;
    public ICommand FindCommand { get; private set; } = null!;
    public ICommand FindNextCommand { get; private set; } = null!;
    public ICommand FindPreviousCommand { get; private set; } = null!;
    public ICommand GoToLineCommand { get; private set; } = null!;
    public ICommand ToggleBookmarkCommand { get; private set; } = null!;
    public ICommand NextBookmarkCommand { get; private set; } = null!;
    public ICommand PreviousBookmarkCommand { get; private set; } = null!;
    public ICommand ClearBookmarksCommand { get; private set; } = null!;
    public ICommand CollapseAllCommand { get; private set; } = null!;
    public ICommand ExpandAllCommand { get; private set; } = null!;
    public ICommand ToggleThemeCommand { get; private set; } = null!;
    public ICommand ToggleFileTreeCommand { get; private set; } = null!;
    public ICommand ToggleMarkdownCommand { get; private set; } = null!;
    public ICommand ToggleBookmarkBarCommand { get; private set; } = null!;
    public ICommand ExitCommand { get; private set; } = null!;
    public ICommand AboutCommand { get; private set; } = null!;
    public ICommand ToggleWordWrapCommand { get; private set; } = null!;
    public ICommand ToggleWhitespaceCommand { get; private set; } = null!;
    public ICommand ToggleEolCommand { get; private set; } = null!;
    public ICommand ZoomInCommand { get; private set; } = null!;
    public ICommand ZoomOutCommand { get; private set; } = null!;
    public ICommand ZoomResetCommand { get; private set; } = null!;
    public ICommand SaveAllCommand { get; private set; } = null!;
    public ICommand CloseFileCommand { get; private set; } = null!;
    public ICommand CloseAllCommand { get; private set; } = null!;
    public ICommand CloseOtherCommand { get; private set; } = null!;
    public ICommand CloseLeftCommand { get; private set; } = null!;
    public ICommand CloseRightCommand { get; private set; } = null!;
    public ICommand ReloadFileCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;
    public ICommand InsertDateCommand { get; private set; } = null!;
    public ICommand StatsCommand { get; private set; } = null!;
    public ICommand OpenCmdCommand { get; private set; } = null!;
    public ICommand MarkAllCommand { get; private set; } = null!;
    public ICommand FormatJsonCommand { get; private set; } = null!;
    public ICommand MinifyJsonCommand { get; private set; } = null!;
    public ICommand ConvertToHtmlCommand { get; private set; } = null!;
    public ICommand FormatXmlCommand { get; private set; } = null!;
    public ICommand MinifyXmlCommand { get; private set; } = null!;
    public ICommand ToggleIndentGuideCommand { get; private set; } = null!;
    public ICommand LightThemeCommand { get; private set; } = null!;
    public ICommand DarkThemeCommand { get; private set; } = null!;
    public ICommand OpenSettingsFolderCommand { get; private set; } = null!;
    public ICommand ClearHighlightCommand { get; private set; } = null!;
    public ICommand ToggleTabLockCommand { get; private set; } = null!;
    public ICommand ToggleTopmostCommand { get; private set; } = null!;

    public MainWindow()
    {
        SettingsService.Load();

        InitializeComponent();
        InitializeDocking();
        EnableFileDrop(this);
        DataContext = this;
        InitializeCommands();
        BuildEncodingMenu();
        BuildFormatMenu();
        BuildLanguageMenu();
        BuildFileTreeContextMenu();
        ApplySettingsToWindow();

        // 恢复上次会话中的文件
        bool restored = false;
        foreach (var path in SettingsService.Current.OpenFiles.ToList())
        {
            if (File.Exists(path))
            {
                OpenFile(path);
                restored = true;
            }
        }

        // 命令行打开文件
        if (!string.IsNullOrEmpty(App.PendingOpenFile))
        {
            OpenFile(App.PendingOpenFile);
            App.PendingOpenFile = null;
        }
        else if (!restored)
        {
            NewFile();
        }

        // 窗口加载完成后再次应用主题，确保整个可视树刷新
        Loaded += (_, _) => { ApplyThemeToUI(); _readyForStatusMessages = true; ScheduleStartupUpdateCheck(); };
    }

    // ==================== VS 停靠内核 ====================

    /// <summary>把 VS 的 ViewManager 挂到 DockHost：主题 + 布局配置 + 文档标签组</summary>
    private void InitializeDocking()
    {
        var vm = ViewManager.Instance;
        vm.Initialize(DockHost);

        Application.Current.Resources.MergedDictionaries.Add(VsShellBootstrap.LoadDockTheme());

        _windowProfile = WindowProfile.Create("Default");
        foreach (var element in _windowProfile.FindAll<ViewElement>())
            element.IsVisible = true;
        vm.WindowProfile = _windowProfile;
        vm.ActiveViewChanged += OnVsActiveViewChanged;

        CommandManager.RegisterClassCommandBinding(typeof(FloatingWindow),
            new CommandBinding(VsDockCommands.CloseFloatingView,
                OnCloseFloatingViewCommand, OnCanCloseFloatingViewCommand));

        DockHost.AddHandler(DragUndockHeader.DragHeaderContextMenuEvent,
            new EventHandler<DragUndockHeaderContextMenuEventArgs>(OnTabHeaderContextMenu));

        FloatingWindowManager.FloatingWindowShown += (_, e) =>
        {
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

    private bool _headerDragActive;
    private EditorControl? _pendingFocusEditor;
    private bool _pendingFileTreeRefresh;

    private void FlushDeferredActivation()
    {
        if (!_headerDragActive) return;
        _headerDragActive = false;
        if (_pendingFileTreeRefresh)
        {
            _pendingFileTreeRefresh = false;
            RefreshFileTreeForCurrentFile();
        }
        if (_pendingFocusEditor != null)
        {
            var editor = _pendingFocusEditor;
            _pendingFocusEditor = null;
            editor.FocusEditor();
        }
    }

    private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // 外部文件拖放：Scintilla 自己在子窗口上注册了 IDropTarget（管文本拖拽），
    // WinForms 的 AllowDrop 抢不过它；而 OLE 在目标拒绝文件拖放时会退回传统的 WM_DROPFILES，
    // 沿父链找注册过 DragAcceptFiles 的窗口投递 —— 实测在编辑器区域落下也能收到，且不干扰文本拖拽。
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? buffer, uint bufferCount);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    private const int WM_DROPFILES = 0x0233;
    private readonly HashSet<Window> _dropHookedWindows = new();

    /// <summary>让某个顶层窗口（主窗口 / VS 停靠的浮动窗）能接收外部文件拖放</summary>
    private void EnableFileDrop(Window window)
    {
        if (!_dropHookedWindows.Add(window)) return;
        window.Closed += (_, _) => _dropHookedWindows.Remove(window);

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

    private IntPtr DropWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES) return IntPtr.Zero;
        var paths = ReadDroppedFiles(wParam);
        if (paths.Count > 0) Dispatcher.BeginInvoke(new Action(() => OpenDroppedPaths(paths)));
        return IntPtr.Zero;
    }

    private static List<string> ReadDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        if (hDrop == IntPtr.Zero) return paths;
        try
        {
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
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

    private long _lastRaiseTick;
    private readonly HashSet<Window> _raiseHookedWindows = new();

    /// <summary>
    /// 把窗口抬到桌面上所有窗口之上。有些窗口（浏览器/Electron 类）会不断把自己按回最前，
    /// 普通 SetWindowPos(HWND_TOP) 甚至系统激活都压不住它；先挂 TOPMOST 再退回 NOTOPMOST
    /// 这一对调用能把它挤下去，而且窗口不会真的留在置顶带里（图钉关闭时也一样）。
    /// </summary>
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
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    private void RaiseAboveEverything()
    {
        // 拖标签/拖窗格期间抬层会打断停靠反馈，正是之前一闪一闪的成因。
        // 松手事件可能被 Scintilla 子 HWND 吃掉，所以以真实的按键状态为准，别让这个标志永久挡住抬层。
        if (_headerDragActive && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) FlushDeferredActivation();
        if (_headerDragActive) return;

        long now = Environment.TickCount64;
        if (now - _lastRaiseTick < 200) return;
        _lastRaiseTick = now;
        RaiseWindowAboveEverything(this);
    }

    private void HookRaiseOnActivate(Window window)
    {
        if (!_raiseHookedWindows.Add(window)) return;
        window.Activated += (_, _) => RaiseWindowAboveEverything(window);
        window.Closed += (_, _) => _raiseHookedWindows.Remove(window);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(RaiseWndProc);
    }

    private const int WM_MOUSEACTIVATE = 0x0021, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONDBLCLK = 0x00A3;

    /// <summary>本窗口已经是前台、只是被别的窗口盖住时不会再触发 Activated，只能从鼠标消息里补抬层</summary>
    private IntPtr RaiseWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_MOUSEACTIVATE or WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK) RaiseAboveEverything();
        return IntPtr.Zero;
    }

    /// <summary>浮动窗默认尺寸过小（约 300x200）且可能落在屏幕外，这里放大并夹回工作区</summary>
    private static void NormalizeFloatingWindow(FloatingWindow window)
    {
        if (window.WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        if (work.Width < 1 || work.Height < 1) return;

        double preferredWidth = Math.Min(Math.Max(work.Width * 0.55, 480), 960);
        double preferredHeight = Math.Min(Math.Max(work.Height * 0.55, 340), 720);

        double width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        window.Width = Math.Clamp(width, preferredWidth, work.Width);
        window.Height = Math.Clamp(height, preferredHeight, work.Height);

        // 标题栏留在工作区内，否则窗口拖不回来
        double left = double.IsNaN(window.Left) ? work.Left : window.Left;
        double top = double.IsNaN(window.Top) ? work.Top : window.Top;
        window.Left = Math.Clamp(left, work.Left, work.Left + work.Width - window.Width);
        window.Top = Math.Clamp(top, work.Top, work.Top + work.Height - window.Height);
    }

    /// <summary>新文档进当前活动的标签组；活动项在浮动窗口里时回主窗格的标签组</summary>
    private DocumentGroup ActiveDocumentGroup()
    {
        var active = ViewManager.Instance.ActiveView;
        if (active != null && !FloatSite.IsFloating(active) && active.Parent is DocumentGroup group)
            return group;

        return DockOperations.GetPrimaryDocumentGroup(_windowProfile!)
            ?? throw new InvalidOperationException("停靠布局里没有文档标签组");
    }

    private View AttachView(DocumentLayoutItem item, EditorControl editor)
    {
        var view = View.Create(_windowProfile!, "doc_" + Guid.NewGuid().ToString("N"));
        view.Title = item;
        view.Content = editor;
        view.DocumentTabTitleTemplate = (DataTemplate)FindResource("DocumentTabHeaderTemplate");
        // 文档只能停进文档标签组，拖到边缘时由 VS 拆出新的文档组（而不是工具窗口式的停靠组）
        view.DockRestriction = DockRestrictionType.DocumentGroup;
        view.IsVisibleChanged += OnViewIsVisibleChanged;

        _itemViews[item] = view;
        _viewItems[view] = item;

        view.IsVisible = true;
        ActiveDocumentGroup().Children.Add(view);
        view.IsSelected = true;
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        return view;
    }

    private void DetachView(DocumentLayoutItem item)
    {
        if (!_itemViews.TryGetValue(item, out var view)) return;

        view.IsVisibleChanged -= OnViewIsVisibleChanged;
        view.Detach();
        view.Content = null;
        _viewItems.Remove(view);
        _itemViews.Remove(item);
    }

    private DocumentLayoutItem? GetActiveItem()
    {
        var view = ViewManager.Instance.ActiveView;
        if (view != null && _viewItems.TryGetValue(view, out var item)) return item;
        return _currentDocument != null ? GetTab(_currentDocument) : null;
    }

    private void InitializeCommands()
    {
        NewFileCommand = new RelayCommand(NewFile);
        OpenFileCommand = new RelayCommand(OpenFile);
        SaveFileCommand = new RelayCommand(SaveFile);
        SaveAsCommand = new RelayCommand(SaveAs);
        UndoCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.Undo());
        RedoCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.Redo());
        CutCommand = new RelayCommand(() => GetCurrentEditor()?.SafeCut());
        CopyCommand = new RelayCommand(() => GetCurrentEditor()?.SafeCopy());
        PasteCommand = new RelayCommand(() => GetCurrentEditor()?.SafePaste());
        SelectAllCommand = new RelayCommand(() => GetCurrentEditor()?.Scintilla.SelectAll());
        ToggleCommentCommand = new RelayCommand(ToggleComment);
        UpperCaseCommand = new RelayCommand(() => GetCurrentEditor()?.UpperCaseSelection());
        LowerCaseCommand = new RelayCommand(() => GetCurrentEditor()?.LowerCaseSelection());
        FindCommand = new RelayCommand(ShowFindReplace);
        FindNextCommand = new RelayCommand(FindNext);
        FindPreviousCommand = new RelayCommand(FindPrevious);
        GoToLineCommand = new RelayCommand(GoToLine);
        ToggleBookmarkCommand = new RelayCommand(ToggleBookmark);
        NextBookmarkCommand = new RelayCommand(NextBookmark);
        PreviousBookmarkCommand = new RelayCommand(PreviousBookmark);
        ClearBookmarksCommand = new RelayCommand(ClearBookmarks);
        CollapseAllCommand = new RelayCommand(() => GetCurrentEditor()?.CollapseAll());
        ExpandAllCommand = new RelayCommand(() => GetCurrentEditor()?.ExpandAll());
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleFileTreeCommand = new RelayCommand(ToggleFileTree);
        ToggleMarkdownCommand = new RelayCommand(ToggleMarkdown);
        ToggleBookmarkBarCommand = new RelayCommand(ToggleBookmarkBar);
        ExitCommand = new RelayCommand(() => Close());
        AboutCommand = new RelayCommand(ShowAbout);
        ToggleWordWrapCommand = new RelayCommand(ToggleWordWrap);
        ToggleWhitespaceCommand = new RelayCommand(ToggleWhitespace);
        ToggleEolCommand = new RelayCommand(ToggleEol);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ZoomResetCommand = new RelayCommand(ZoomReset);
        SaveAllCommand = new RelayCommand(SaveAll);
        CloseFileCommand = new RelayCommand(CloseCurrentFile);
        CloseAllCommand = new RelayCommand(CloseAllFiles);
        CloseOtherCommand = new RelayCommand(CloseOtherFiles);
        CloseLeftCommand = new RelayCommand(CloseLeftFiles);
        CloseRightCommand = new RelayCommand(CloseRightFiles);
        ReloadFileCommand = new RelayCommand(ReloadFile);
        OpenFolderCommand = new RelayCommand(OpenCurrentFileFolder);
        InsertDateCommand = new RelayCommand(InsertDateTime);
        StatsCommand = new RelayCommand(ShowStats);
        OpenCmdCommand = new RelayCommand(OpenCmdInCurrentDir);
        MarkAllCommand = new RelayCommand(() => MarkAllLines());
        FormatJsonCommand = new RelayCommand(FormatJson);
        MinifyJsonCommand = new RelayCommand(MinifyJson);
        ConvertToHtmlCommand = new RelayCommand(ConvertToHtml);
        FormatXmlCommand = new RelayCommand(FormatXml);
        MinifyXmlCommand = new RelayCommand(MinifyXml);
        ToggleIndentGuideCommand = new RelayCommand(ToggleIndentGuide);
        LightThemeCommand = new RelayCommand(() => SetTheme(ThemeService.ThemeMode.Light));
        DarkThemeCommand = new RelayCommand(() => SetTheme(ThemeService.ThemeMode.Dark));
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        ClearHighlightCommand = new RelayCommand(() => GetCurrentEditor()?.ClearFindHighlight());
        ToggleTabLockCommand = new RelayCommand(ToggleTabLock);
        ToggleTopmostCommand = new RelayCommand(ToggleTopmost);
    }

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

    private void ApplySettingsToWindow()
    {
        var s = SettingsService.Current;

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

        ThemeService.SetTheme(s.Theme == "Dark" ? ThemeService.ThemeMode.Dark : ThemeService.ThemeMode.Light);
        ApplyThemeToUI();

        FileTreePanel.Visibility = s.ShowFileTree ? Visibility.Visible : Visibility.Collapsed;
        MarkdownPanel.Visibility = s.ShowMarkdown ? Visibility.Visible : Visibility.Collapsed;
        BookmarkBarPanel.Visibility = s.ShowBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
        if (s.ShowFileTree) LoadFileTree();

        MenuWordWrap.IsChecked = s.WordWrap;
        MenuWhitespace.IsChecked = s.ShowWhitespace;
        MenuEol.IsChecked = s.ShowEol;

        _recentFiles.Clear();
        _recentFiles.AddRange(s.RecentFiles.Where(File.Exists));
        RecentFilesList.ItemsSource = null;
        RecentFilesList.ItemsSource = _recentFiles;
        RefreshRecentFilesMenu();
    }

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

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (ctrl && !shift && e.Key == Key.N) { NewFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.O) { OpenFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.S) { SaveFile(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { SaveAs(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F) { ShowFindReplace(); e.Handled = true; }
        else if (e.Key == Key.F3 && !shift) { FindNext(); e.Handled = true; }
        else if (e.Key == Key.F3 && shift) { FindPrevious(); e.Handled = true; }
        else if (e.Key == Key.F2 && !alt) { ToggleBookmark(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.G) { GoToLine(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.W) { CloseCurrentFile(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.T) { ToggleTheme(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F) { FormatJson(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.M) { MinifyJson(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.A) { ShowDirFind(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.R) { ShowCompare(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.R) { ToggleReadOnly(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.H) { ShowHash(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.T) { ToggleTabLock(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.P) { ToggleTopmost(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.F) { RunFindAllShortcut(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.K) { OpenBatchFind(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.C) { OpenColumnEditor(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.D) { DuplicateCurrentLine(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.J) { JoinLines(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.L) { RemoveEmptyLines(false); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.D) { DeleteCurrentLine(); e.Handled = true; }
        else if (alt && shift && e.Key == Key.Up) { MoveCurrentLineUp(); e.Handled = true; }
        else if (alt && shift && e.Key == Key.Down) { MoveCurrentLineDown(); e.Handled = true; }
    }

    private void HandleShortcut(string cmd)
    {
        switch (cmd)
        {
            case "new": NewFile(); break;
            case "open": OpenFile(); break;
            case "save": SaveFile(); break;
            case "saveas": SaveAs(); break;
            case "find": ShowFindReplace(); break;
            case "findnext": OnFindNextRequested(null, EventArgs.Empty); break;
            case "findprev": OnFindPreviousRequested(null, EventArgs.Empty); break;
            case "gotoline": GoToLine(); break;
            case "closefile": CloseCurrentFile(); break;
            case "bookmark": ToggleBookmark(); break;
            case "comment": ToggleComment(); break;
            case "theme": ToggleTheme(); break;
            case "zoomin": ZoomIn(); break;
            case "zoomout": ZoomOut(); break;
            case "zoomreset": ZoomReset(); break;
        }
    }

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
            foreach (var path in dlg.FileNames)
            {
                OpenFile(path);
            }
        }
    }

    private void OpenFile(string filePath)
    {
        OpenFile(filePath, reloadEncoding: null);
    }

    /// <summary>
    /// 打开文件。reloadEncoding 非空表示「以指定编码重新载入」（ndd 的编码-重新载入语义）。
    /// </summary>
    private void OpenFile(string filePath, (int CodePage, bool Bom)? reloadEncoding)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);

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

            if (EncodingService.LooksBinary(bytes))
            {
                if (!AskOpenAsHex(filePath)) return;
                OpenHexView(filePath, bytes);
                return;
            }

            var bigFile = SettingsService.Current.BigFileThresholdMb;
            if (bytes.Length > bigFile * 1024L * 1024L && !AskOpenLargeFile(filePath, bytes.Length))
                return;

            var (codePage, bom) = reloadEncoding ?? EncodingService.Detect(bytes);
            string content = EncodingService.Decode(bytes, codePage, stripBom: true);

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

            var tab = AddDocument(doc);
            var editor = _itemEditors[tab];

            editor.LoadContent(content);
            editor.SetLexerForFile(filePath);
            editor.CommentPrefix = LexerService.GetCommentPrefix(filePath);
            editor.ApplyTheme();
            editor.ReadOnly = doc.IsReadOnly;

            UpdateStatusBar(editor);
            AddRecentFile(filePath);
            RefreshFileTreeForCurrentFile();
            WatchFile(filePath);

            if (reloadEncoding != null)
                StatusBarMessage($"已用 {EncodingService.GetDisplayName(codePage, bom)} 重新载入");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool AskOpenAsHex(string filePath)
    {
        var r = MessageBox.Show(this,
            $"\"{Path.GetFileName(filePath)}\" 含有无法作为文本编辑的内容（二进制）。\n\n是否以十六进制方式查看？",
            "二进制文件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    private bool AskOpenLargeFile(string filePath, long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        var r = MessageBox.Show(this,
            $"\"{Path.GetFileName(filePath)}\" 体积较大（约 {mb:F1} MB），载入会明显变慢。\n\n是否继续载入？",
            "大文件提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

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

        var tab = AddDocument(doc);
        var editor = _itemEditors[tab];
        editor.LoadContent(HexDump.FromBytes(bytes));
        editor.SetLexerByName("null");
        editor.ReadOnly = true;
        UpdateStatusBar(editor);
        AddRecentFile(filePath);
    }

    private DocumentLayoutItem AddDocument(Document doc)
    {
        var item = new DocumentLayoutItem(doc);

        var editor = new EditorControl();

        // 应用主题与显示选项
        editor.ApplyTheme();
        var s = SettingsService.Current;
        editor.WordWrap = s.WordWrap;
        editor.ShowWhitespace = s.ShowWhitespace;
        editor.ShowEol = s.ShowEol;
        editor.CommentPrefix = LexerService.GetCommentPrefix(doc.FilePath);

        // 事件
        editor.CursorPositionChanged += (_, _) => UpdateStatusBar(editor);
        editor.ZoomChanged += (_, _) => UpdateStatusBar(editor);
        editor.SavePointChanged += (_, isSaved) =>
        {
            doc.IsDirty = !isSaved;
            UpdateTabHeader(doc);
            UpdateStatusBar(editor);
        };
        editor.BookmarkToggled += (_, _) => UpdateBookmarkBar(editor);
        editor.ShortcutPressed += (_, cmd) => HandleShortcut(cmd);
        editor.ReadOnlyEditAttempted += OnReadOnlyEditAttempted;
        editor.FilesDropped += paths => OpenDroppedPaths(paths.ToList());

        _documentItems[doc] = item;
        _itemEditors[item] = editor;
        _documents.Add(doc);

        AttachView(item, editor);
        _currentDocument = doc;
        EmptyStateText.Visibility = Visibility.Collapsed;

        editor.FocusEditor();
        return item;
    }

    private DocumentLayoutItem? GetTab(Document doc)
        => _documentItems.TryGetValue(doc, out var item) ? item : null;

    private void SelectDocument(Document doc)
    {
        if (!_documentItems.TryGetValue(doc, out var item)) return;
        if (!_itemViews.TryGetValue(item, out var view)) return;

        if (view.Parent is ViewGroup parent)
            parent.SelectedElement = view;
        view.IsSelected = true;
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
    }

    /// <summary>窗格结构变动后统一刷新"当前文档"</summary>
    private void SyncCurrentDocument()
    {
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

        EmptyStateText.Visibility = _documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseTab(DocumentLayoutItem item)
    {
        var doc = item.Document;

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
                _currentDocument = doc;
                SaveFile();
                if (doc.IsDirty) return; // 保存失败（用户取消另存为）
            }
        }

        DetachView(item);
        _documents.Remove(doc);
        _documentItems.Remove(doc);
        _itemEditors.Remove(item);
        if (doc.FilePath.Length > 0) UnwatchFile(doc.FilePath);
        SyncCurrentDocument();
    }

    private EditorControl? GetCurrentEditor()
    {
        if (_currentDocument != null && _documentItems.TryGetValue(_currentDocument, out var item))
        {
            return _itemEditors.GetValueOrDefault(item);
        }
        return null;
    }

    /// <summary>关闭标签但不询问保存（用于「以指定编码重新载入」等场景）</summary>
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

    private DateTime _statusMessageUntil = DateTime.MinValue;

    /// <summary>在状态栏显示一条临时提示（8 秒后被常规信息覆盖）</summary>
    private void StatusBarMessage(string message)
    {
        _statusMessageUntil = DateTime.Now.AddSeconds(8);
        StatusMessage.Text = message;
        StatusMessage.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");

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

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    private void WatchFile(string filePath)
    {
        if (!SettingsService.Current.WatchExternalChanges) return;
        if (_watchers.ContainsKey(filePath)) return;

        string? dir = Path.GetDirectoryName(filePath);
        string name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir, name)
            {
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

    private void UnwatchFile(string filePath)
    {
        if (_watchers.Remove(filePath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void BeginExternalChangeCheck(string filePath)
    {
        Dispatcher.BeginInvoke(() => OnExternalFileChanged(filePath));
    }

    private void BeginFileDeletedCheck(string filePath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var doc = _documents.FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (doc == null) return;
            StatusBarMessage($"磁盘上已删除：{doc.DisplayName}");
        });
    }

    private void OnExternalFileChanged(string filePath)
    {
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
            var lastWrite = File.GetLastWriteTime(filePath);
            long length = new FileInfo(filePath).Length;
            if (lastWrite == doc.DiskTimestamp && length == doc.DiskLength) return;

            var editor = GetTab(doc) is { } tab ? _itemEditors.GetValueOrDefault(tab) : null;
            bool dirty = doc.IsDirty;

            var result = MessageBox.Show(this,
                dirty
                    ? $"\"{doc.DisplayName}\" 在磁盘上已被其他程序修改，且你当前有未保存的改动。\n\n" +
                      "「是」= 放弃你的改动，从磁盘重新载入\n「否」= 保留当前内容，忽略这次变更"
                    : $"\"{doc.DisplayName}\" 在磁盘上已被其他程序修改。\n\n是否重新载入最新内容？",
                "外部变更", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes && editor != null)
            {
                ReloadFromDisk(doc, editor);
            }
        }
        catch
        {
            // 监控本身出错不该打断编辑
        }
    }

    private void SaveFile()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        if (string.IsNullOrEmpty(doc.FilePath))
        {
            SaveAs();
            return;
        }

        if (doc.IsHexView)
        {
            MessageBox.Show(this, "十六进制视图是只读的，无法保存。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            WriteToDisk(doc, editor.GetContent(), doc.FilePath);
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
    private void WriteToDisk(Document doc, string content, string path)
    {
        EncodingService.WriteFile(path, content, doc.CodePage, doc.HasBom);
        doc.DiskTimestamp = File.GetLastWriteTime(path);
        doc.DiskLength = new FileInfo(path).Length;
    }

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
            UnwatchFile(doc.FilePath);
            doc.FilePath = dlg.FileName;
            doc.DisplayName = Path.GetFileName(dlg.FileName);
            editor.Scintilla.SetSavePoint();
            doc.IsDirty = false;
            editor.SetLexerForFile(dlg.FileName);
            editor.CommentPrefix = LexerService.GetCommentPrefix(dlg.FileName);
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

    private void ToggleComment()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        var prefix = LexerService.GetCommentPrefix(_currentDocument?.FilePath ?? "");
        editor.ToggleComment(prefix);
    }

    // ==================== 格式菜单（对齐 ndd 的行/列/大小写操作） ====================

    /// <summary>格式类操作的前提：有编辑器、非只读、非十六进制视图</summary>
    private EditorControl? FormatEditor()
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return null;

        if (doc.IsHexView)
        {
            StatusBarMessage("十六进制视图不支持格式操作");
            return null;
        }
        if (doc.IsReadOnly)
        {
            StatusBarMessage("文档处于只读模式（Ctrl+Alt+R 取消）");
            return null;
        }
        return editor;
    }

    private void SortLines(EditorControl.SortMode mode) => FormatEditor()?.SortLines(mode);
    private void RemoveConsecutiveDuplicateLines() => FormatEditor()?.RemoveConsecutiveDuplicateLines();
    private void RemoveDuplicateLines() => FormatEditor()?.RemoveDuplicateLines();
    private void RemoveDuplicateLinesSorted() => FormatEditor()?.RemoveDuplicateLinesSorted();

    /// <summary>keepOne=true 时把连续空行压成一行（ndd 的 Cbc 变体），否则全删</summary>
    private void RemoveEmptyLines(bool keepOne)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        if (keepOne) editor.RemoveConsecutiveEmptyLines();
        else editor.RemoveEmptyLines();
    }

    private void RemoveHeadEndEmptyLines() => FormatEditor()?.RemoveHeadEndEmptyLines();
    private void RemoveLeadingSpace() => FormatEditor()?.RemoveLeadingSpace();
    private void RemoveTrailingSpace() => FormatEditor()?.RemoveTrailingSpace();
    private void RemoveAllSpace() => FormatEditor()?.RemoveAllSpace();

    private void InsertBlankLineAbove() => FormatEditor()?.InsertBlankLineAbove();
    private void InsertBlankLineBelow() => FormatEditor()?.InsertBlankLineBelow();
    private void DuplicateCurrentLine() => FormatEditor()?.DuplicateCurrentLine();
    private void MoveCurrentLineUp() => FormatEditor()?.MoveCurrentLineUp();
    private void MoveCurrentLineDown() => FormatEditor()?.MoveCurrentLineDown();
    private void DeleteCurrentLine() => FormatEditor()?.DeleteCurrentLine();
    private void DeleteToLineStart() => FormatEditor()?.DeleteToLineStart();
    private void DeleteToLineEnd() => FormatEditor()?.DeleteToLineEnd();
    private void CutCurrentLine() => FormatEditor()?.CutCurrentLine();
    private void CopyCurrentLine() => GetCurrentEditor()?.CopyCurrentLine();
    private void CopyToLineEnd() => GetCurrentEditor()?.CopyToLineEnd();
    private void JoinLines() => FormatEditor()?.JoinLines();
    private void SplitLines() => FormatEditor()?.SplitLines();
    private void PadLinesToEqualWidth() => FormatEditor()?.PadLinesToEqualWidth();

    private void ChangeCase(EditorControl.CaseMode mode) => FormatEditor()?.ChangeCase(mode);

    private void TabsToSpaces()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        editor.TabsToSpaces(leadingOnly: _menuTabLeadingOnly?.IsChecked == true);
    }

    private void SpacesToTabsMenu(bool leadingOnly)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        editor.SpacesToTabs(leadingOnly);
    }

    private void BlockComment(bool lineComment)
    {
        var editor = FormatEditor();
        if (editor == null) return;

        string path = _currentDocument?.FilePath ?? string.Empty;
        if (lineComment)
        {
            string prefix = LexerService.GetCommentPrefix(path);
            if (string.IsNullOrEmpty(prefix))
            {
                StatusBarMessage("当前语言没有行注释前缀");
                return;
            }
            editor.BlockComment(prefix);
            return;
        }

        var pair = LexerService.GetStreamComment(path);
        if (pair == null)
        {
            StatusBarMessage("当前语言没有流式注释");
            return;
        }
        // StreamComment 自身是开关式的：已包住就拆开
        editor.StreamComment(pair.Value.Begin, pair.Value.End);
    }

    private void BlockUncomment()
    {
        var editor = FormatEditor();
        if (editor == null) return;
        string prefix = LexerService.GetCommentPrefix(_currentDocument?.FilePath ?? string.Empty);
        if (string.IsNullOrEmpty(prefix)) { StatusBarMessage("当前语言没有行注释前缀"); return; }
        editor.BlockUncomment(prefix);
    }

    private void ConvertEol(ScintillaNET.Eol eol)
    {
        var editor = FormatEditor();
        if (editor == null) return;
        editor.ConvertEol(eol);
        UpdateStatusBar(editor);
    }

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

        editor.ColumnEditor(dlg.Mode, dlg.Text, dlg.Initial, dlg.Increment, dlg.Repeat,
                            dlg.Digits, dlg.NumberFormat, dlg.LeadingZero, dlg.Prefix);
        SettingsService.Save();
    }

    private void ToggleColumnMode()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ColumnMode = !editor.ColumnMode;
        _menuColumnMode.IsChecked = editor.ColumnMode;
        StatusBarMessage(editor.ColumnMode ? "列编辑模式已开启：拖选即为矩形选区" : "列编辑模式已关闭");
    }

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

    private void OpenUrlAtCaret()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!editor.OpenUrlAtCaret()) StatusBarMessage("光标所在行没有找到 URL 或邮箱");
    }

    private void AutoCompleteFromDocument() => GetCurrentEditor()?.AutoCompleteFromDocument();

    private void CopyBookmarkedLines() => GetCurrentEditor()?.CopyLinesWithBookmarks();
    private void DeleteBookmarkedLines() => FormatEditor()?.DeleteLinesWithBookmarks();
    private void InvertBookmarks() => FormatEditor()?.ToggleBookmarksOthers();

    private MenuItem _menuColumnMode = null!;
    private MenuItem _menuTabLeadingOnly = null!;

    /// <summary>
    /// 用代码生成「格式」菜单：项目里同类菜单太长，XAML 写 40 个 Click 处理器不划算，
    /// 而这里的项目惯例（编码/语言菜单）也是代码生成的。
    /// </summary>
    private void BuildFormatMenu()
    {
        FormatMenu.Items.Clear();

        _menuColumnMode = new MenuItem { Header = "列编辑模式", IsCheckable = true };
        _menuColumnMode.Click += (_, _) => ToggleColumnMode();

        _menuTabLeadingOnly = new MenuItem { Header = "仅处理行首", IsCheckable = true, IsChecked = SettingsService.Current.TabLeadingOnly };
        _menuTabLeadingOnly.Click += (_, _) =>
        {
            SettingsService.Current.TabLeadingOnly = _menuTabLeadingOnly.IsChecked;
            SettingsService.Save();
        };

        static MenuItem Item(string header, Action action, string gesture = "")
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }
        static Separator Sep() => new();

        var sortMenu = new MenuItem { Header = "排序(_S)" };
        sortMenu.Items.Add(Item("行按字典序升序", () => SortLines(EditorControl.SortMode.LexAsc)));
        sortMenu.Items.Add(Item("行按字典序降序", () => SortLines(EditorControl.SortMode.LexDesc)));
        sortMenu.Items.Add(Item("忽略大小写升序", () => SortLines(EditorControl.SortMode.LexAscIgnoreCase)));
        sortMenu.Items.Add(Item("忽略大小写降序", () => SortLines(EditorControl.SortMode.LexDescIgnoreCase)));
        sortMenu.Items.Add(Sep());
        sortMenu.Items.Add(Item("按整数升序", () => SortLines(EditorControl.SortMode.IntegerAsc)));
        sortMenu.Items.Add(Item("按整数降序", () => SortLines(EditorControl.SortMode.IntegerDesc)));
        sortMenu.Items.Add(Item("按小数升序（小数点）", () => SortLines(EditorControl.SortMode.DecimalDotAsc)));
        sortMenu.Items.Add(Item("按小数降序（小数点）", () => SortLines(EditorControl.SortMode.DecimalDotDesc)));
        sortMenu.Items.Add(Item("按小数升序（逗号）", () => SortLines(EditorControl.SortMode.DecimalCommaAsc)));
        sortMenu.Items.Add(Item("按小数降序（逗号）", () => SortLines(EditorControl.SortMode.DecimalCommaDesc)));
        sortMenu.Items.Add(Sep());
        sortMenu.Items.Add(Item("倒序排列行", () => SortLines(EditorControl.SortMode.ReverseOrder)));
        sortMenu.Items.Add(Item("随机打乱行", () => SortLines(EditorControl.SortMode.Randomize)));
        FormatMenu.Items.Add(sortMenu);

        var dedupMenu = new MenuItem { Header = "去重(_D)" };
        dedupMenu.Items.Add(Item("删除相邻重复行", RemoveConsecutiveDuplicateLines));
        dedupMenu.Items.Add(Item("删除重复行（保留顺序）", RemoveDuplicateLines));
        dedupMenu.Items.Add(Item("删除重复行（去重后排序）", RemoveDuplicateLinesSorted));
        FormatMenu.Items.Add(dedupMenu);

        var blankMenu = new MenuItem { Header = "空行(_B)" };
        blankMenu.Items.Add(Item("删除所有空行", () => RemoveEmptyLines(false), "Ctrl+Alt+L"));
        blankMenu.Items.Add(Item("连续空行压成一行", () => RemoveEmptyLines(true)));
        blankMenu.Items.Add(Item("删除首尾空行", RemoveHeadEndEmptyLines));
        FormatMenu.Items.Add(blankMenu);

        var blankCharMenu = new MenuItem { Header = "空白字符(_W)" };
        blankCharMenu.Items.Add(Item("删除行首空白", RemoveLeadingSpace));
        blankCharMenu.Items.Add(Item("删除行尾空白", RemoveTrailingSpace));
        blankCharMenu.Items.Add(Item("删除行首行尾空白", RemoveAllSpace));
        blankCharMenu.Items.Add(Sep());
        blankCharMenu.Items.Add(Item("空格转制表符（整行）", () => SpacesToTabsMenu(leadingOnly: false)));
        blankCharMenu.Items.Add(Item("空格转制表符（仅行首）", () => SpacesToTabsMenu(leadingOnly: true)));
        blankCharMenu.Items.Add(Item("制表符转空格", TabsToSpaces));
        blankCharMenu.Items.Add(_menuTabLeadingOnly);
        FormatMenu.Items.Add(blankCharMenu);

        var lineMenu = new MenuItem { Header = "当前行(_L)" };
        lineMenu.Items.Add(Item("在上方插入空行", InsertBlankLineAbove));
        lineMenu.Items.Add(Item("在下方插入空行", InsertBlankLineBelow));
        lineMenu.Items.Add(Item("复制当前行", DuplicateCurrentLine, "Ctrl+Alt+D"));
        lineMenu.Items.Add(Item("上移当前行", MoveCurrentLineUp, "Alt+Shift+↑"));
        lineMenu.Items.Add(Item("下移当前行", MoveCurrentLineDown, "Alt+Shift+↓"));
        lineMenu.Items.Add(Item("删除当前行", DeleteCurrentLine, "Ctrl+Shift+D"));
        lineMenu.Items.Add(Sep());
        lineMenu.Items.Add(Item("剪切到行尾", CutCurrentLine));
        lineMenu.Items.Add(Item("复制到行尾", CopyToLineEnd));
        lineMenu.Items.Add(Item("复制到整行", CopyCurrentLine));
        lineMenu.Items.Add(Item("删除到行首", DeleteToLineStart));
        lineMenu.Items.Add(Item("删除到行尾", DeleteToLineEnd));
        lineMenu.Items.Add(Sep());
        lineMenu.Items.Add(Item("合并选中行", JoinLines, "Ctrl+Alt+J"));
        lineMenu.Items.Add(Item("按显示宽度拆分行", SplitLines));
        lineMenu.Items.Add(Item("各行补齐到相同宽度", PadLinesToEqualWidth));
        FormatMenu.Items.Add(lineMenu);

        var caseMenu = new MenuItem { Header = "大小写(_C)" };
        caseMenu.Items.Add(Item("全部大写", () => ChangeCase(EditorControl.CaseMode.Upper)));
        caseMenu.Items.Add(Item("全部小写", () => ChangeCase(EditorControl.CaseMode.Lower)));
        caseMenu.Items.Add(Item("首字母大写（单词）", () => ChangeCase(EditorControl.CaseMode.Proper)));
        caseMenu.Items.Add(Item("首字母大写（混合）", () => ChangeCase(EditorControl.CaseMode.ProperBlend)));
        caseMenu.Items.Add(Item("句首大写", () => ChangeCase(EditorControl.CaseMode.Sentence)));
        caseMenu.Items.Add(Item("句首大写（混合）", () => ChangeCase(EditorControl.CaseMode.SentenceBlend)));
        caseMenu.Items.Add(Item("反转大小写", () => ChangeCase(EditorControl.CaseMode.Invert)));
        caseMenu.Items.Add(Item("随机大小写", () => ChangeCase(EditorControl.CaseMode.Random)));
        FormatMenu.Items.Add(caseMenu);

        var eolMenu = new MenuItem { Header = "行尾符(_E)" };
        eolMenu.Items.Add(Item("Windows: CR LF", () => ConvertEol(ScintillaNET.Eol.CrLf)));
        eolMenu.Items.Add(Item("Mac: CR", () => ConvertEol(ScintillaNET.Eol.Cr)));
        eolMenu.Items.Add(Item("Unix: LF", () => ConvertEol(ScintillaNET.Eol.Lf)));
        FormatMenu.Items.Add(eolMenu);

        var bookmarkMenu = new MenuItem { Header = "书签行(_M)" };
        bookmarkMenu.Items.Add(Item("复制所有书签行", CopyBookmarkedLines));
        bookmarkMenu.Items.Add(Item("删除所有书签行", DeleteBookmarkedLines));
        bookmarkMenu.Items.Add(Item("反转书签", InvertBookmarks));
        FormatMenu.Items.Add(bookmarkMenu);

        FormatMenu.Items.Add(Sep());
        FormatMenu.Items.Add(_menuColumnMode);
        FormatMenu.Items.Add(Item("列编辑器…", OpenColumnEditor, "Alt+C"));
        FormatMenu.Items.Add(Item("给选中各行行首加光标", () => GetCurrentEditor()?.AddCaretsToLineStarts()));
        FormatMenu.Items.Add(Item("把选区粘贴到每一行行首", () => GetCurrentEditor()?.PasteToEachSelectedLine()));
        FormatMenu.Items.Add(Sep());
        FormatMenu.Items.Add(Item("块注释", () => BlockComment(lineComment: true)));
        FormatMenu.Items.Add(Item("取消块注释", () => BlockUncomment()));
        FormatMenu.Items.Add(Item("流式注释 /* */", () => BlockComment(lineComment: false)));
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

    private void ShowFindReplace()
    {
        FindReplacePanel.Visibility = Visibility.Visible;

        var editor = GetCurrentEditor();
        if (editor != null)
        {
            var sel = editor.SelectedText;
            if (!string.IsNullOrEmpty(sel) && sel.Length < 200 && !sel.Contains('\n'))
            {
                FindReplacePanel.SetFindText(sel);
            }
            else
            {
                FindReplacePanel.SetFindText(_findText);
            }
        }
    }

    private void OnFindRequested(object sender, FindReplacePanel.FindReplaceArgs e)
    {
        _findText = e.FindText;
        _replaceText = e.ReplaceText;
        _matchCase = e.MatchCase;
        _matchWord = e.MatchWord;
        _useRegex = e.Regex;

        var editor = GetCurrentEditor();
        if (editor == null || string.IsNullOrEmpty(_findText)) return;

        int count = editor.HighlightAllMatches(_findText, _matchCase, _matchWord, _useRegex);
        FindReplacePanel.SetMatchCount(count);
        FindNext();
    }

    private void OnFindNextRequested(object? sender, EventArgs e)
    {
        var args = FindReplacePanel.GetArgsPublic();
        if (args != null)
        {
            _findText = args.FindText;
            _matchCase = args.MatchCase;
            _matchWord = args.MatchWord;
            _useRegex = args.Regex;
        }
        FindNext();
    }

    private void OnFindPreviousRequested(object? sender, EventArgs e)
    {
        var args = FindReplacePanel.GetArgsPublic();
        if (args != null)
        {
            _findText = args.FindText;
            _matchCase = args.MatchCase;
            _matchWord = args.MatchWord;
            _useRegex = args.Regex;
        }
        FindPrevious();
    }

    private void OnReplaceRequested(object sender, FindReplacePanel.FindReplaceArgs e)
    {
        _findText = e.FindText;
        _replaceText = e.ReplaceText;
        _matchCase = e.MatchCase;
        _matchWord = e.MatchWord;
        _useRegex = e.Regex;

        var editor = GetCurrentEditor();
        if (editor == null || string.IsNullOrEmpty(_findText)) return;

        editor.ReplaceCurrent(_findText, _replaceText, _matchCase, _matchWord, _useRegex);
    }

    private void OnReplaceAllRequested(object sender, FindReplacePanel.FindReplaceArgs e)
    {
        _findText = e.FindText;
        _replaceText = e.ReplaceText;
        _matchCase = e.MatchCase;
        _matchWord = e.MatchWord;
        _useRegex = e.Regex;

        var editor = GetCurrentEditor();
        if (editor == null || string.IsNullOrEmpty(_findText)) return;

        int count = editor.ReplaceAll(_findText, _replaceText, _matchCase, _matchWord, _useRegex);
        int remaining = editor.HighlightAllMatches(_findText, _matchCase, _matchWord, _useRegex);
        FindReplacePanel.SetMatchCount(remaining);
        MessageBox.Show(this, $"替换完成，共替换 {count} 处。", "替换", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnFindCloseRequested(object? sender, EventArgs e)
    {
        FindReplacePanel.Visibility = Visibility.Collapsed;
        var editor = GetCurrentEditor();
        editor?.ClearFindHighlight();
        editor?.FocusEditor();
    }

    // ==================== 目录查找 / 替换 ====================

    private void ShowDirFind()
    {
        var doc = _currentDocument;
        string root = doc?.FilePath.Length > 0
            ? Path.GetDirectoryName(doc.FilePath) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;

        string selected = GetCurrentEditor()?.SelectedText ?? string.Empty;
        DirFindPanel.Visibility = Visibility.Visible;
        DirFindPanel.Prepare(root, selected.Length is > 0 and < 100 ? selected : null);
    }

    private void OnShowDirFind(object sender, RoutedEventArgs e) => ShowDirFind();

    private void OnDirFindCloseRequested(object? sender, EventArgs e)
    {
        DirFindPanel.Visibility = Visibility.Collapsed;
        GetCurrentEditor()?.FocusEditor();
    }

    /// <summary>双击结果：必要时先打开文件，再选中文本并滚动到可见处</summary>
    private void OnDirMatchJumpRequested(object? sender, DirMatch match)
    {
        Document? doc = _documentItems.Keys.FirstOrDefault(
            d => string.Equals(d.FilePath, match.FilePath, StringComparison.OrdinalIgnoreCase));

        if (doc == null)
        {
            OpenFile(match.FilePath);
            doc = _currentDocument;
        }
        else
        {
            SelectDocument(doc);
        }

        var editor = doc == null ? null : GetEditor(doc);
        if (editor == null) return;
        editor.GoToLine(match.Line - 1);
        editor.SelectInLine(match.Line - 1, match.Column, match.Length);
        editor.FocusEditor();
        StatusBarMessage($"{Path.GetFileName(match.FilePath)} 行 {match.Line}");
    }

    /// <summary>目录替换写盘后，未修改过的同名标签页重读磁盘，避免屏幕上还是旧内容</summary>
    private void OnDirReplaceCompleted(object? sender, IReadOnlyList<string> changedPaths)
    {
        var affected = changedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int reloaded = 0;
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
    private void ReloadFromDiskQuietly(Document doc, EditorControl editor)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
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

    private EditorControl? GetEditor(Document doc)
        => _documentItems.TryGetValue(doc, out var tab) && _itemEditors.TryGetValue(tab, out var editor) ? editor : null;

    // ==================== 文件对比 ====================

    private CompareWindow? _compareWindow;

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
            OpenFileRequested = path => OpenFile(path),
        };
        _compareWindow.Closed += (_, _) => _compareWindow = null;
        _compareWindow.Show();
        return _compareWindow;
    }

    private void ShowCompare()
    {
        EnsureCompareWindow();
        StatusBarMessage("在对比窗口里选择左右两侧文件（F7 跳到下一个差异）");
    }

    private void OnCompareFiles(object sender, RoutedEventArgs e) => ShowCompare();

    /// <summary>ndd 的「与磁盘版本对比」：左侧是编辑区（可能含未保存改动），右侧是文件现状</summary>
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

    private void OnCompareWithDisk(object sender, RoutedEventArgs e) => CompareWithDisk();

    // ==================== 目录对比 ====================

    private DirCompareWindow? _dirCompareWindow;

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

    private static string FindWinMerge()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            string candidate = Path.Combine(root, "WinMerge", "WinMergeU.exe");
            if (File.Exists(candidate)) return candidate;
        }

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

    private ColumnEditWindow? _columnEditWindow;

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

    private void OnInsertLineNumbers(object sender, RoutedEventArgs e)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var (startLine, endLine, _, _) = editor.GetColumnEditContext();
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

    private HashWindow? _hashWindow;

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

    private void OnShowHash(object sender, RoutedEventArgs e) => ShowHash();

    // ==================== 当前文件查找全部 / 批量查找 ====================

    /// <summary>结果列表对应的是哪个文档，跳转时即使已切标签也要回到它</summary>
    private Document? _searchHitsOwner;

    private void OnFindAllInFile(object sender, RoutedEventArgs e)
    {
        // 查找面板里已有关键字时直接查，不再多问一次
        if (!string.IsNullOrWhiteSpace(_findText))
        {
            RunInFileSearch(new[] { _findText }, _matchCase, _useRegex);
            return;
        }
        OpenBatchFind();
    }

    private void OnBatchFind(object sender, RoutedEventArgs e) => OpenBatchFind();

    private void RunFindAllShortcut() => OnFindAllInFile(this, new RoutedEventArgs());

    private void OpenBatchFind()
    {
        string seed = GetCurrentEditor()?.SelectedText ?? string.Empty;
        if (seed.Contains('\n')) seed = string.Empty;
        var win = new BatchFindWindow(seed, _matchCase, _useRegex) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (!string.IsNullOrWhiteSpace(win.Terms[0])) _findText = win.Terms[0];
        RunInFileSearch(win.Terms, win.MatchCase, win.UseRegex);
    }

    private void RunInFileSearch(IReadOnlyList<string> terms, bool matchCase, bool useRegex)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

        var hits = InFileSearch.FindAll(editor.GetContent(), terms, matchCase, useRegex);
        _searchHitsOwner = doc;
        SearchResultsPanel.ShowResults($"{doc.DisplayName} · {terms.Count} 个关键词", hits);
        StatusBarMessage($"查找全部：{hits.Count} 处命中");

        // 多关键词逐个调用会互相清掉高亮，只在单关键词时铺满编辑器
        if (terms.Count == 1) editor.HighlightAllMatches(terms[0], matchCase, false, useRegex);
    }

    private void OnSearchHitJumpRequested(SearchHit hit)
    {
        var doc = _searchHitsOwner ?? _currentDocument;
        if (doc == null) return;
        if (!ReferenceEquals(doc, _currentDocument)) SelectDocument(doc);

        var editor = GetEditor(doc);
        if (editor == null) return;
        editor.GoToLine(hit.Line - 1);
        editor.SelectInLine(hit.Line - 1, hit.Column - 1, hit.Length);
        editor.FocusEditor();
        StatusBarMessage($"行 {hit.Line}，列 {hit.Column}：{hit.Term}");
    }

    private void OnSearchResultsCloseRequested()
    {
        SearchResultsPanel.ClearResults();
        _searchHitsOwner = null;
        GetCurrentEditor()?.ClearFindHighlight();
    }

    private void FindNext()
    {
        var editor = GetCurrentEditor();
        if (editor == null || string.IsNullOrEmpty(_findText)) return;
        editor.FindNext(_findText, _matchCase, _matchWord, _useRegex);
    }

    private void FindPrevious()
    {
        var editor = GetCurrentEditor();
        if (editor == null || string.IsNullOrEmpty(_findText)) return;
        editor.FindPrevious(_findText, _matchCase, _matchWord, _useRegex);
    }

    // ==================== 跳转到行 ====================

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

    private void ToggleBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ToggleBookmark(editor.CurrentLine);
        UpdateBookmarkBar(editor);
    }

    private void NextBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        var line = editor.NextBookmark(editor.CurrentLine);
        if (line != null) editor.GoToLine(line.Value);
    }

    private void PreviousBookmark()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        var line = editor.PreviousBookmark(editor.CurrentLine);
        if (line != null) editor.GoToLine(line.Value);
    }

    private void ClearBookmarks()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.ClearBookmarks();
        UpdateBookmarkBar(editor);
    }

    private void UpdateBookmarkBar(EditorControl editor)
    {
        if (BookmarkBarPanel.Visibility != Visibility.Visible) return;

        var bookmarks = editor.GetBookmarks();
        BookmarkItems.ItemsSource = bookmarks
            .Select(l => new BookmarkItem(l, $"行 {l + 1}"))
            .ToList();
    }

    private void OnBookmarkItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BookmarkItems.SelectedItem is BookmarkItem item)
        {
            GetCurrentEditor()?.GoToLine(item.Line);
            BookmarkItems.SelectedItem = null;
        }
    }

    private class BookmarkItem
    {
        public int Line { get; }
        public string Text { get; }
        public BookmarkItem(int line, string text) { Line = line; Text = text; }
    }

    // ==================== 主题 ====================

    private void ToggleTheme()
    {
        ThemeService.ToggleTheme();
        ApplyThemeToUI();
    }

    private void ApplyThemeToUI()
    {
        bool dark = ThemeService.CurrentTheme == ThemeService.ThemeMode.Dark;

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

    private void ToggleWordWrap()
    {
        bool v = !(GetCurrentEditor()?.WordWrap ?? false);
        foreach (var e in _itemEditors.Values) e.WordWrap = v;
        MenuWordWrap.IsChecked = v;
        SettingsService.Current.WordWrap = v;
    }

    private void ToggleWhitespace()
    {
        bool v = !(GetCurrentEditor()?.ShowWhitespace ?? false);
        foreach (var e in _itemEditors.Values) e.ShowWhitespace = v;
        MenuWhitespace.IsChecked = v;
        SettingsService.Current.ShowWhitespace = v;
    }

    private void ToggleEol()
    {
        bool v = !(GetCurrentEditor()?.ShowEol ?? false);
        foreach (var e in _itemEditors.Values) e.ShowEol = v;
        MenuEol.IsChecked = v;
        SettingsService.Current.ShowEol = v;
    }

    private void ZoomIn()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ZoomIn();
        UpdateStatusBar(e);
    }

    private void ZoomOut()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ZoomOut();
        UpdateStatusBar(e);
    }

    private void ZoomReset()
    {
        var e = GetCurrentEditor();
        if (e == null) return;
        e.ResetZoom();
        UpdateStatusBar(e);
    }

    private void ToggleIndentGuide()
    {
        bool v = !(GetCurrentEditor()?.ShowIndentGuides ?? true);
        foreach (var e in _itemEditors.Values) e.ShowIndentGuides = v;
        MenuIndentGuide.IsChecked = v;
    }

    private void SetTheme(ThemeService.ThemeMode mode)
    {
        ThemeService.SetTheme(mode);
        ApplyThemeToUI();
    }

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

    // ==================== 保存/关闭 ====================

    private void SaveAll()
    {
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

    private void CloseCurrentFile()
    {
        var item = GetActiveItem();
        if (item != null) CloseTab(item);
    }

    private void CloseAllFiles()
    {
        foreach (var doc in _documents.ToList())
            CloseTab(_documentItems[doc]);
    }

    private void CloseOtherFiles()
    {
        var keep = GetActiveItem();
        foreach (var doc in _documents.ToList())
        {
            var item = _documentItems[doc];
            if (!ReferenceEquals(item, keep)) CloseTab(item);
        }
    }

    private void CloseLeftFiles()
    {
        var active = GetActiveItem();
        if (active == null || !_itemViews.TryGetValue(active, out var activeView)) return;
        if (activeView.Parent is not DocumentGroup group) return;

        var siblings = group.Children.OfType<View>().ToList();
        int idx = siblings.IndexOf(activeView);
        if (idx <= 0) return;
        for (int i = idx - 1; i >= 0; i--)
        {
            if (_viewItems.TryGetValue(siblings[i], out var item)) CloseTab(item);
        }
    }

    private void CloseRightFiles()
    {
        var active = GetActiveItem();
        if (active == null || !_itemViews.TryGetValue(active, out var activeView)) return;
        if (activeView.Parent is not DocumentGroup group) return;

        var siblings = group.Children.OfType<View>().ToList();
        int idx = siblings.IndexOf(activeView);
        if (idx < 0) return;
        for (int i = siblings.Count - 1; i > idx; i--)
        {
            if (_viewItems.TryGetValue(siblings[i], out var item)) CloseTab(item);
        }
    }

    /// <summary>锁定当前标签：防误关，也锁住编码切换（对齐 ndd 的标签固定语义）</summary>
    private void ToggleTabLock()
    {
        var doc = _currentDocument;
        if (doc == null) return;
        doc.IsLocked = !doc.IsLocked;
        UpdateTabHeader(doc);
        StatusBarMessage(doc.IsLocked ? $"已锁定标签：{doc.DisplayName}" : $"已解锁标签：{doc.DisplayName}");
    }

    private void ToggleTopmost() => Topmost = !Topmost;

    /// <summary>置顶要盖到程序的每一个窗口：VS 停靠的浮动窗格、目录对比等是独立顶层窗口，不跟主窗口的 Z 序带</summary>
    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        foreach (Window w in Application.Current.Windows)
            if (!ReferenceEquals(w, this)) w.Topmost = Topmost;

        SettingsService.Current.AlwaysOnTop = Topmost;
        SettingsService.Save();
        if (_readyForStatusMessages)
            StatusBarMessage(Topmost ? "已固定置顶：本程序在所有窗口最前" : "已取消固定置顶");
    }

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
    private void ReloadFromDisk(Document doc, EditorControl editor)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(doc.FilePath);
            editor.LoadContent(EncodingService.Decode(bytes, doc.CodePage, stripBom: true));
            doc.IsDirty = false;
            doc.DiskTimestamp = File.GetLastWriteTime(doc.FilePath);
            doc.DiskLength = bytes.LongLength;
            editor.SetLexerForFile(doc.FilePath);
            editor.CommentPrefix = LexerService.GetCommentPrefix(doc.FilePath);
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

    private void InsertDateTime()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        editor.Scintilla.ReplaceSelection(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private void ShowStats()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var content = editor.GetContent();
        var lines = editor.LineCount;
        var chars = editor.TextLength;
        var nonWhitespace = content.Count(c => !char.IsWhiteSpace(c));
        var words = Regex.Matches(content, @"[一-龥]|[A-Za-z0-9_]+").Count;

        MessageBox.Show(this,
            $"行数：{lines}\n字符数：{chars}\n不含空白字符数：{nonWhitespace}\n单词数：{words}",
            "字数统计", MessageBoxButton.OK, MessageBoxImage.Information);
    }

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

    private static readonly System.Text.Json.JsonSerializerOptions JsonIndentOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly System.Text.Json.JsonSerializerOptions JsonCompactOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void FormatJson() => ProcessJson(JsonIndentOptions, "格式化 JSON");

    private void MinifyJson() => ProcessJson(JsonCompactOptions, "压缩 JSON");

    private void ProcessJson(System.Text.Json.JsonSerializerOptions options, string action)
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;
        if (!TryGetWorkRange(editor, out string content, out int start, out int end, action)) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var text = System.Text.Json.JsonSerializer.Serialize(doc.RootElement, options);

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

    private void FormatXml() => ProcessXml(false, "格式化 XML");

    private void MinifyXml() => ProcessXml(true, "压缩 XML");

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
            if (doc.Declaration is { } decl) text = decl.ToString() + "\n" + text;
            else if (content.TrimStart().StartsWith("<?xml")) text = FirstLine(content.TrimStart()) + "\n" + text;

            editor.Scintilla.SetSelection(start, end);
            editor.Scintilla.ReplaceSelection(text);

            MessageBox.Show(this, action + "完成。", action, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Xml.XmlException ex)
        {
            MessageBox.Show(this, "XML 解析失败（第 " + ex.LineNumber + " 行）：" + ex.Message,
                action + "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, action + "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FirstLine(string text)
    {
        int idx = text.IndexOf('\n');
        return idx < 0 ? text : text[..idx].TrimEnd('\r');
    }

    // ==================== Markdown / 文本 转 HTML ====================

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
            ed.SetLexerForFile("converted.html");
            ed.CommentPrefix = "<!--";
            ed.ApplyTheme();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "转 HTML 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 菜单构建 ====================

    private void BuildEncodingMenu()
    {
        ConvertEncodingMenu.Items.Clear();
        foreach (var opt in EncodingService.ConvertOptions)
        {
            var item = new MenuItem { Header = opt.Display };
            var captured = opt;
            item.Click += (_, _) => ConvertEncoding(captured);
            ConvertEncodingMenu.Items.Add(item);
        }

        ReloadEncodingMenu.Items.Clear();
        foreach (var opt in EncodingService.ReloadOptions)
        {
            var item = new MenuItem { Header = opt.Display };
            var captured = opt;
            item.Click += (_, _) => ReloadEncoding(captured);
            ReloadEncodingMenu.Items.Add(item);
        }
    }

    private void ConvertEncoding(EncodingOption option)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

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

        // ndd 语义：转换只改「保存时用哪种编码」，屏幕内容不动，因此文档变为待保存状态
        doc.CodePage = option.CodePage;
        doc.HasBom = option.Bom;
        doc.IsDirty = true;
        UpdateTabHeader(doc);
        UpdateStatusBar(editor);
        StatusBarMessage($"已转为 {option.Display}（保存后写入磁盘）");
    }

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

    private void OnToggleBom(object sender, RoutedEventArgs e)
    {
        var doc = _currentDocument;
        var editor = GetCurrentEditor();
        if (doc == null || editor == null) return;

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

    private void OnToggleReadOnly(object sender, RoutedEventArgs e) => ToggleReadOnly();

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

    private void OnReadOnlyEditAttempted(object? sender, EventArgs e)
    {
        StatusBarMessage("文档是只读的（Ctrl+Alt+R 可取消只读）");
    }

    // 括号里第三个值是给词法器注入关键字用的「样例扩展名」：Lexilla 没有独立的 JS/Go/TOML 词法器，
    // 这些语言借用近似词法器（cpp/props），关键字表必须按语言注入，否则关键字不上色
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
                StatusLang.Text = $"语法: {(lexer == "null" ? "text" : display.ToLowerInvariant())}";
            };
            LanguageMenu.Items.Add(item);
        }
    }

    // ==================== 视图切换 ====================

    private void ToggleFileTree()
    {
        FileTreePanel.Visibility = FileTreePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

        if (FileTreePanel.Visibility == Visibility.Visible && FileTree.Items.Count == 0)
        {
            LoadFileTree();
        }
    }

    private void ToggleMarkdown()
    {
        MarkdownPanel.Visibility = MarkdownPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

        if (MarkdownPanel.Visibility == Visibility.Visible)
            UpdateMarkdownPreview();
    }

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

    private void UpdateMarkdownPreview()
    {
        var editor = GetCurrentEditor();
        if (editor == null) return;

        var content = editor.GetContent();
        MarkdownText.Text = MarkdownService.RenderToPlainText(content);
    }

    // ==================== 文件树 ====================

    private string _fileTreeRoot = string.Empty;

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

    private void RefreshFileTreeForCurrentFile()
    {
        if (FileTreePanel.Visibility != Visibility.Visible) return;
        if (_currentDocument == null || string.IsNullOrEmpty(_currentDocument.FilePath)) return;

        var dir = Path.GetDirectoryName(_currentDocument.FilePath);
        if (!string.IsNullOrEmpty(dir) && dir != _fileTreeRoot)
        {
            SetFileTreeRoot(dir);
        }
    }

    // ==================== 文件树右键菜单 ====================

    private void BuildFileTreeContextMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => OnOpenTreeNode(false);
        menu.Items.Add(open);

        var openWin = new MenuItem { Header = "在新窗口打开" };
        openWin.Click += (_, _) => OnOpenTreeNode(true);
        menu.Items.Add(openWin);

        menu.Items.Add(new Separator());

        var copyAbs = new MenuItem { Header = "复制绝对路径" };
        copyAbs.Click += (_, _) => CopyToClipboard(GetSelectedNodePath() ?? string.Empty);
        menu.Items.Add(copyAbs);

        var copyRel = new MenuItem { Header = "复制相对路径" };
        copyRel.Click += (_, _) =>
        {
            var node = FileTree.SelectedItem as FileNode;
            if (node == null || _fileTreeRoot.Length == 0) return;
            var rel = node.FullPath;
            if (rel.StartsWith(_fileTreeRoot, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(_fileTreeRoot.Length).TrimStart('\\');
            CopyToClipboard(rel);
        };
        menu.Items.Add(copyRel);

        menu.Items.Add(new Separator());

        var newFile = new MenuItem { Header = "新建文件" };
        newFile.Click += (_, _) => CreateInSelectedFolder("新建文件.txt");
        menu.Items.Add(newFile);

        var newFolder = new MenuItem { Header = "新建文件夹" };
        newFolder.Click += (_, _) => CreateInSelectedFolder("新建文件夹");
        menu.Items.Add(newFolder);

        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => OnRenameTreeNode();
        menu.Items.Add(rename);

        var del = new MenuItem { Header = "删除" };
        del.Click += (_, _) => OnDeleteTreeNode();
        menu.Items.Add(del);

        FileTree.ContextMenu = menu;
    }

    private string? GetSelectedNodePath()
    {
        return (FileTree.SelectedItem as FileNode)?.FullPath;
    }

    private string GetSelectedNodeDir()
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null) return _fileTreeRoot;
        return node.IsDirectory ? node.FullPath : (Path.GetDirectoryName(node.FullPath) ?? _fileTreeRoot);
    }

    private void OnOpenTreeNode(bool newWindow)
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null || node.IsDirectory) return;

        if (newWindow)
        {
            var win = new MainWindow();
            win.Show();
            win.OpenFile(node.FullPath);
        }
        else
        {
            OpenFile(node.FullPath);
        }
    }

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

    private void OnRenameTreeNode()
    {
        var node = FileTree.SelectedItem as FileNode;
        if (node == null) return;

        var input = Prompt.ShowInputDialog(this, "重命名", "新名称：", node.Name);
        if (string.IsNullOrWhiteSpace(input)) return;

        var newName = input.Trim();
        var newPath = Path.Combine(Path.GetDirectoryName(node.FullPath) ?? ".", newName);
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

    private FileNode? BuildDirectoryNode(string path)
    {
        if (!Directory.Exists(path))
            return null;

        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            name = path;

        var node = new FileNode(path, name, true);

        try
        {
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

    private void OnFileTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode node && !node.IsDirectory)
        {
            OpenFile(node.FullPath);
        }
    }

    private void OnRecentFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentFilesList.SelectedItem is string path)
        {
            RecentFilesList.SelectedItem = null;
            OpenFile(path);
        }
    }

    private readonly List<string> _recentFiles = new();

    private void AddRecentFile(string filePath)
    {
        _recentFiles.Remove(filePath);
        _recentFiles.Insert(0, filePath);
        if (_recentFiles.Count > 20)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);

        RecentFilesList.ItemsSource = null;
        RecentFilesList.ItemsSource = _recentFiles;
        RefreshRecentFilesMenu();
    }

    // ==================== 状态栏 ====================

    private void UpdateStatusBar(EditorControl editor)
    {
        // 载入/重载入时非当前标签的编辑器也会触发事件，若不拦截会把别的文档的统计写进状态栏
        if (!ReferenceEquals(editor, GetCurrentEditor())) return;

        StatusLineCol.Text = $"行: {editor.CurrentLine + 1} 列: {editor.CurrentColumn + 1}";
        StatusSel.Text = $"选中: {editor.SelectionLength}";
        StatusChars.Text = $"字符: {editor.TextLength}";
        StatusLines.Text = $"行数: {editor.LineCount}";
        StatusZoom.Text = $"缩放率: {100 + editor.ZoomLevel * 10}%";
        StatusEol.Text = editor.EolName;

        if (_currentDocument != null)
        {
            StatusEncoding.Text = EncodingService.GetDisplayName(_currentDocument.CodePage, _currentDocument.HasBom);

            var lang = _currentDocument.FilePath.Length > 0
                ? LexerService.GetLanguageName(_currentDocument.FilePath)
                : "Text";
            StatusLang.Text = $"语法: {lang.ToLowerInvariant()}";
            StatusModified.Text = _currentDocument.IsDirty ? "●" : "";
            StatusReadonly.Text = _currentDocument.IsReadOnly
                ? (_currentDocument.IsHexView ? "十六进制" : "只读")
                : string.Empty;
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
                Dispatcher.BeginInvoke(new Action(UpdateMarkdownPreview), DispatcherPriority.Background);
            }
        }
    }

    private void UpdateTabHeader(Document doc)
    {
        if (_documentItems.TryGetValue(doc, out var item))
            item.RefreshHeader();
    }

    private void OnTabHeaderButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 不拦截的话，VS 的 DragUndockHeader 会把点击按钮当成拖拽起点
        if (e.ChangedButton == MouseButton.Left) e.Handled = true;
    }

    private void OnTabLockPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Button { Tag: DocumentLayoutItem item }) return;
        e.Handled = true;
        SelectDocument(item.Document);
        ToggleTabLock();
    }

    private void OnTabClosePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Button { Tag: DocumentLayoutItem item }) return;
        e.Handled = true;
        CloseTab(item);
    }

    // ==================== 停靠事件 ====================

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
    private void OnCanCloseFloatingViewCommand(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = e.Parameter is ViewElement;

    private void OnCloseFloatingViewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ViewElement element) return;
        var views = element is View single ? new List<View> { single } : element.FindAll<View>().ToList();
        foreach (var view in views)
        {
            if (_viewItems.TryGetValue(view, out var item)) CloseTab(item);
        }
    }

    /// <summary>视图被隐藏时走正常关闭流程；取消关闭则恢复显示</summary>
    private void OnViewIsVisibleChanged(object? sender, EventArgs e)
    {
        if (sender is not View view || view.IsVisible || _closingFromVisibility) return;
        if (!_viewItems.TryGetValue(view, out var item)) return;

        _closingFromVisibility = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _closingFromVisibility = false;
            if (!_itemViews.ContainsKey(item)) return;
            CloseTab(item);
            if (_itemViews.ContainsKey(item) && !view.IsVisible)
                view.IsVisible = true;
        }), DispatcherPriority.Background);
    }

    /// <summary>标签右键菜单（DragUndockHeader 只发事件，不会自己弹菜单）</summary>
    private void OnTabHeaderContextMenu(object? sender, DragUndockHeaderContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DragUndockHeader header || header.ViewElement is not View view) return;
        if (!_viewItems.TryGetValue(view, out var item)) return;

        var menu = new ContextMenu
        {
            PlacementTarget = header,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        void Add(string text, Action action)
        {
            var entry = new MenuItem { Header = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

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
        Add("浮动为独立窗口", () => FloatItem(item));
        Add("停靠回主窗口", () => DockItem(item));
        Add("向右新建标签组", () => NewTabGroup(item, toRight: true));
        Add("向下新建标签组", () => NewTabGroup(item, toRight: false));

        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>菜单：把当前标签拖出主窗口，变成可自由移动的浮动窗口</summary>
    private void OnFloatCurrentTab(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) FloatItem(item);
    }

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
    private void OnDockCurrentTab(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) DockItem(item);
    }

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

    private void OnNewVerticalTabGroup(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) NewTabGroup(item, toRight: true);
    }

    private void OnNewHorizontalTabGroup(object sender, RoutedEventArgs e)
    {
        if (GetActiveItem() is { } item) NewTabGroup(item, toRight: false);
    }

    /// <summary>把指定标签拆到新的标签组：toRight=true 左右并排，false 上下排列</summary>
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
        if (group.Children.Count <= 1)
        {
            StatusBarMessage("当前标签组里只有这一个标签，先把别的标签拖进来再拆");
            return;
        }

        var newGroup = DockOperations.CreateDocumentGroup(group,
            toRight ? Orientation.Horizontal : Orientation.Vertical);
        DockOperations.Dock(newGroup, view, DockDirection.Fill);
        ViewManager.Instance.SetActiveView(view, ActivationType.Default);
        StatusBarMessage(toRight ? "已向右新建标签组" : "已向下新建标签组");
    }

    /// <summary>视图菜单：所有文档回到主标签组，关闭浮动窗口，回收空标签组</summary>
    private void OnResetDockLayout(object sender, RoutedEventArgs e)
    {
        if (_windowProfile == null) return;
        var main = DockOperations.GetMainSiteDocumentGroup(_windowProfile)
            ?? DockOperations.GetPrimaryDocumentGroup(_windowProfile);
        if (main == null) return;

        foreach (var view in _windowProfile.FindAll<View>().ToList())
        {
            if (FloatSite.IsFloating(view))
                DockOperations.Dock(main, view, DockDirection.Fill);
        }

        foreach (var view in _windowProfile.FindAll<View>().ToList())
        {
            if (view.Parent is DocumentGroup group && !ReferenceEquals(group, main))
                DockOperations.Dock(main, view, DockDirection.Fill);
        }

        if (_currentDocument != null) SelectDocument(_currentDocument);
        StatusBarMessage("已把全部标签停靠回主窗口");
    }

    // ==================== 关闭 ====================

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
            foreach (var doc in modified)
            {
                _currentDocument = doc;
                SaveFile();
            }
            if (_documents.Any(d => d.IsDirty))
                e.Cancel = true;
        }
    }

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

    private void SaveSettings()
    {
        var s = SettingsService.Current;
        s.Theme = ThemeService.CurrentTheme == ThemeService.ThemeMode.Dark ? "Dark" : "Light";

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
        s.OpenFiles = _documents
            .Where(d => !string.IsNullOrEmpty(d.FilePath))
            .Select(d => d.FilePath)
            .ToList();

        SettingsService.Save();
    }

    // ==================== 自动更新 ====================

    private bool _startupUpdateChecked;

    /// <summary>启动后静默查一次：没更新、断网都不出声，只有确实有新版本才打扰</summary>
    private void ScheduleStartupUpdateCheck()
    {
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(5000);
            await Services.UpdateService.CheckForUpdatesAsync(this, showUpToDateDialog: false, StatusBarMessage);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await Services.UpdateService.CheckForUpdatesAsync(this, showUpToDateDialog: true, StatusBarMessage);
    }

    // ==================== 关于 ====================

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
            "参考项目：Notepad-- (ndd)\n" +
            "https://gitee.com/cxasm/notepad--",
            "关于 NotepadSharp", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== RelayCommand ====================

    private class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
