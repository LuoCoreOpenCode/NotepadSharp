using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace NotepadSharp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// 应用入口：真正的窗口由 App.xaml 的 StartupUri 创建（MainWindow.xaml），这里只负责
    /// 「在第一个窗口出现之前」必须完成的全局准备——停靠内核解析器、代码页编码、全局异常兜底、
    /// 命令行传入的待打开文件。放在这里而不是 MainWindow 构造函数，是因为这些都必须是进程级、且只做一次。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// WPF 启动回调：在 MainWindow 被创建并显示之前执行，顺序敏感（见方法内注释）。
        /// </summary>
        /// <param name="e">StartupEventArgs，Args 是操作系统传进来的命令行参数（双击/「打开方式」时是文件全路径）</param>
        protected override void OnStartup(StartupEventArgs e)
        {
            // 必须早于 MainWindow：停靠内核是 VS 安装目录里的程序集，依赖靠 AssemblyResolve 现场补齐
            Services.VsShellBootstrap.EnsureResolver();

            base.OnStartup(e);

            // 注册代码页编码提供程序（支持 GBK/GB2312 等）
            // .NET 精简后只内置 Unicode/UTF 系，不装这个 provider 时 Encoding.GetEncoding("gbk") 直接抛，
            // 「编码」菜单里那一串中文代码页与「按代码页重新载入」都会失效，所以必须在打开任何文件前注册
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch
            {
                // 忽略
            }

            // 全局未处理异常兜底，避免弹出难懂的崩溃框
            // 两条通道都要挂：DispatcherUnhandledException 只管 UI 线程，
            // 池线程 / await 续体 / 析构里冒出来的异常只会走 AppDomain.CurrentDomain.UnhandledException
            AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowGlobalError(args.ExceptionObject as Exception);
            DispatcherUnhandledException += (_, args) =>
            {
                ShowGlobalError(args.Exception);
                // 标记已处理 = 让 WPF 不再继续往上抛，程序弹完错误框后还能接着用（不终止进程）
                args.Handled = true;
            };

            // 处理命令行参数：直接打开文件
            // 这里只认第一个参数（Windows 的「打开方式」/ 拖到 exe 上就这一个路径），多余的忽略。
            // 不能在这里直接开窗：此时 MainWindow 还没构造，只把路径暂存到 PendingOpenFile，
            // 由 MainWindow 在 Loaded 之后取走并打开（见 MainWindow 的 _restoreFiles 处理逻辑）
            if (e.Args.Length > 0)
            {
                var filePath = e.Args[0];
                // 参数可能是「文件夹」或已失效的路径，先确认确实是文件再暂存
                if (File.Exists(filePath))
                {
                    PendingOpenFile = filePath;
                }
            }
        }

        /// <summary>
        /// 命令行要求启动时打开的文件全路径；纯静态字段，供 MainWindow 跨构造阶段取用。
        /// 消费方（MainWindow 的 Loaded 处理）读完必须自己置回 null，否则窗口重建时会把同一个文件再开一遍。
        /// </summary>
        public static string? PendingOpenFile { get; set; }

        /// <summary>
        /// 全局异常的统一呈现：把「未处理错误 + 堆栈」弹成一个能看懂的对话框，替代 Windows 的崩溃提示。
        /// 这里只尽力报告，不做任何恢复动作，也不落日志（堆栈长就只显示在框里）。
        /// </summary>
        /// <param name="ex">要展示的异常；为 null（如 AppDomain 通道拿不到 Exception 对象）时什么都不做</param>
        private static void ShowGlobalError(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                // 用全名 System.Windows.MessageBox：项目同时引用了 WinForms，
                // 不加限定会解析到 WinForms 那个 MessageBox，弹框的属主与样式都不对
                System.Windows.MessageBox.Show(
                    "程序遇到未处理的错误：\n\n" + ex.Message + "\n\n" + (ex.StackTrace ?? string.Empty),
                    "NotepadSharp 错误",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch
            {
                // 连弹窗都失败则忽略
            }
        }
    }
}
