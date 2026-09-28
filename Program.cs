using System.Windows.Forms;

namespace PlcConectCom.WinFormsTest;

// WinForms 程序入口
static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, args) =>
        {
            LogUnhandledException("UI 线程未处理异常", args.Exception, isTerminating: false);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogUnhandledException("非 UI 线程未处理异常", ex, args.IsTerminating);
        };

        if (!LicenseAuthorization.IsCurrentlyAuthorized(out _, out _)
            && !LicenseAuthorization.EnsureAuthorized(owner: null))
        {
            MessageBox.Show("未通过授权校验或已取消输入，程序将退出。", "授权", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.Run(new HB_HMI_MainForm());
        }
        catch (Exception ex)
        {
            string logPath = LogUnhandledException("启动失败", ex, isTerminating: true);
            MessageBox.Show($"程序启动失败，详情已写入日志：\n{logPath}", "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string LogUnhandledException(string title, Exception ex, bool isTerminating)
    {
        string logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        string logPath = Path.Combine(logDir, "unhandled-exceptions.log");

        try
        {
            Directory.CreateDirectory(logDir);
            string text =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title} IsTerminating={isTerminating}{Environment.NewLine}" +
                ex + Environment.NewLine +
                new string('-', 120) + Environment.NewLine;
            File.AppendAllText(logPath, text);
        }
        catch
        {
            // Last-chance exception logging must never throw.
        }

        return logPath;
    }
}
