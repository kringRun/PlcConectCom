namespace PlcConectCom.WinFormsTest;

/// <summary>MES / SCADA 模块的运行日志（落盘到 Logs\Mes 目录，按天分文件）。</summary>
public static class MesScadaLog
{
    private static readonly object Sync = new();

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "Mes");

    /// <summary>追加一行日志；写失败时静默忽略，不打断 MES 状态机。</summary>
    public static void Append(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"MesController_{DateTime.Today:yyyyMMdd}.txt");
            string line = $"{DateTime.Now:HH:mm:ss.fff} => {message}";

            lock (Sync)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // 日志写失败不影响主流程
        }
    }
}
