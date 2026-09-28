using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PlcConectCom.WinFormsTest;

internal sealed class ApplicationLogEntry
{
    public DateTime LogTime { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 运行日志按日期写入独立 SQLite 文件：Logs/AppLog_yyyy-MM-dd.db
/// </summary>
internal static class ApplicationLogStore
{
    private static readonly object Sync = new();

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS application_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            log_time TEXT NOT NULL,
            log_date TEXT NOT NULL,
            message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_application_log_date_time
            ON application_log (log_date, log_time DESC);
        """;

    public static string LogsDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    public static string GetDatabasePath(DateTime date) =>
        Path.Combine(LogsDirectory, $"AppLog_{date:yyyy-MM-dd}.db");

    public static void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        DateTime now = DateTime.Now;
        string dbPath = GetDatabasePath(now);
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogsDirectory);
                using var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
                connection.Open();
                using (var ensure = connection.CreateCommand())
                {
                    ensure.CommandText = SchemaSql;
                    ensure.ExecuteNonQuery();
                }

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO application_log (log_time, log_date, message)
                    VALUES ($time, $date, $message);
                    """;
                command.Parameters.AddWithValue("$time", now.ToString(TimeFormat, CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$message", message.Trim());
                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // 日志入库失败不影响界面
        }
    }

    public static List<ApplicationLogEntry> Query(DateTime fromDate, DateTime toDate, string? keyword, int maxRows = 10000)
    {
        var results = new List<ApplicationLogEntry>();
        if (toDate.Date < fromDate.Date)
            (fromDate, toDate) = (toDate, fromDate);

        string? keywordTrim = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        int remaining = Math.Max(1, maxRows);

        lock (Sync)
        {
            for (DateTime day = fromDate.Date; day <= toDate.Date; day = day.AddDays(1))
            {
                string path = GetDatabasePath(day);
                if (!File.Exists(path))
                    continue;

                try
                {
                    using var connection = new SqliteConnection($"Data Source={path};Cache=Shared");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT log_time, message
                        FROM application_log
                        WHERE ($keyword IS NULL OR message LIKE $keywordLike)
                        ORDER BY log_time DESC
                        LIMIT $limit;
                        """;
                    command.Parameters.AddWithValue("$keyword", keywordTrim ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("$keywordLike", keywordTrim is null ? DBNull.Value : $"%{keywordTrim}%");
                    command.Parameters.AddWithValue("$limit", remaining);

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        string timeText = reader.GetString(0);
                        string message = reader.GetString(1);
                        if (!TryParseLogTime(timeText, out DateTime logTime))
                            continue;

                        results.Add(new ApplicationLogEntry { LogTime = logTime, Message = message });
                        if (--remaining <= 0)
                            break;
                    }
                }
                catch
                {
                    // 跳过损坏的日志库
                }

                if (remaining <= 0)
                    break;
            }
        }

        results.Sort((a, b) => b.LogTime.CompareTo(a.LogTime));
        if (results.Count > maxRows)
            results.RemoveRange(maxRows, results.Count - maxRows);

        return results;
    }

    private static bool TryParseLogTime(string text, out DateTime value)
    {
        if (DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;

        return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
