using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace NotepadSharp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 必须早于 MainWindow：停靠内核是 VS 安装目录里的程序集，依赖靠 AssemblyResolve 现场补齐
            Services.VsShellBootstrap.EnsureResolver();

            base.OnStartup(e);

            // 注册代码页编码提供程序（支持 GBK/GB2312 等）
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch
            {
                // 忽略
            }

            // 全局未处理异常兜底，避免弹出难懂的崩溃框
            AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowGlobalError(args.ExceptionObject as Exception);
            DispatcherUnhandledException += (_, args) =>
            {
                ShowGlobalError(args.Exception);
                args.Handled = true;
            };

            // 处理命令行参数：直接打开文件
            if (e.Args.Length > 0)
            {
                var filePath = e.Args[0];
                if (File.Exists(filePath))
                {
                    PendingOpenFile = filePath;
                }
            }
        }

        public static string? PendingOpenFile { get; set; }

        private static void ShowGlobalError(Exception? ex)
        {
            if (ex == null) return;
            try
            {
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
