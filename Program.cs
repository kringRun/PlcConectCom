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
            try
            {
                MessageBox.Show(args.Exception.ToString(), "UI 线程未处理异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // ignore
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                try
                {
                    MessageBox.Show(ex.ToString(), "未处理异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // ignore
                }
            }
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
            MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
