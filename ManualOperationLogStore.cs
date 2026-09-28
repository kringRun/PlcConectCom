using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PlcConectCom.WinFormsTest;

/// <summary>手动操作类型：调试按钮页上的三类操作。</summary>
public enum ManualOperationCategory
{
    /// <summary>点击按钮（Step±/JOG/HOME/STOP/使能/停止运动等）。</summary>
    Button = 0,

    /// <summary>修改位置（试教、编辑位置数据、保存位置、快照恢复）。</summary>
    Position = 1,

    /// <summary>点击输出点（输出点位表 ON/OFF 按钮）。</summary>
    Output = 2
}

/// <summary>一条手动操作日志。</summary>
public sealed class ManualOperationLogEntry
{
    public DateTime LogTime { get; init; }

    public ManualOperationCategory Category { get; init; }

    /// <summary>设备名（主 PLC / 子 PLC 名称）。</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>操作对象：轴名 / 位置名 / 输出点位名。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>子对象：位置记录里对应的轴名。</summary>
    public string SubTarget { get; init; } = string.Empty;

    /// <summary>按钮名或操作名。</summary>
    public string Action { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string BeforeValue { get; init; } = string.Empty;

    public string AfterValue { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string Operator { get; init; } = string.Empty;
}

/// <summary>
/// 手动操作日志：独立目录 + 按日期分库，路径为 Logs/ManualOperation/ManualOp_yyyy-MM-dd.db。
/// 与运行日志（ApplicationLogStore → Logs/AppLog_*.db）物理隔离，便于单独归档/清理。
/// </summary>
public static class ManualOperationLogStore
{
    private static readonly object Sync = new();

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string FilePrefix = "ManualOp_";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS manual_operation_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            log_time TEXT NOT NULL,
            log_date TEXT NOT NULL,
            category TEXT NOT NULL,
            device TEXT NOT NULL DEFAULT '',
            target TEXT NOT NULL DEFAULT '',
            sub_target TEXT NOT NULL DEFAULT '',
            action TEXT NOT NULL DEFAULT '',
            address TEXT NOT NULL DEFAULT '',
            before_value TEXT NOT NULL DEFAULT '',
            after_value TEXT NOT NULL DEFAULT '',
            detail TEXT NOT NULL DEFAULT '',
            operator TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_manual_operation_log_cat_time
            ON manual_operation_log (category, log_date, log_time DESC);
        """;

    /// <summary>独立的手动操作日志目录（AppContext.BaseDirectory\Logs\ManualOperation）。</summary>
    public static string LogsDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "ManualOperation");

    /// <summary>当前操作人（角色 + 系统账号），由主窗体注入；未注入时退化为系统账号。</summary>
    public static Func<string>? OperatorProvider { get; set; }

    public static string GetDatabasePath(DateTime date) =>
        Path.Combine(LogsDirectory, $"{FilePrefix}{date:yyyy-MM-dd}.db");

    /// <summary>写入一条日志。任何异常都被吞掉，保证日志失败不影响界面与 PLC 操作。</summary>
    public static void Write(
        ManualOperationCategory category,
        string? device,
        string? action,
        string? target = null,
        string? subTarget = null,
        string? address = null,
        string? beforeValue = null,
        string? afterValue = null,
        string? detail = null)
    {
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
                    INSERT INTO manual_operation_log
                        (log_time, log_date, category, device, target, sub_target, action, address, before_value, after_value, detail, operator)
                    VALUES
                        ($time, $date, $category, $device, $target, $subTarget, $action, $address, $before, $after, $detail, $operator);
                    """;
                command.Parameters.AddWithValue("$time", now.ToString(TimeFormat, CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$category", category.ToString());
                command.Parameters.AddWithValue("$device", Trim(device));
                command.Parameters.AddWithValue("$target", Trim(target));
                command.Parameters.AddWithValue("$subTarget", Trim(subTarget));
                command.Parameters.AddWithValue("$action", Trim(action));
                command.Parameters.AddWithValue("$address", Trim(address));
                command.Parameters.AddWithValue("$before", Trim(beforeValue));
                command.Parameters.AddWithValue("$after", Trim(afterValue));
                command.Parameters.AddWithValue("$detail", Trim(detail));
                command.Parameters.AddWithValue("$operator", Trim(ResolveOperator()));
                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // 日志入库失败不影响界面
        }
    }

    /// <summary>
    /// 按类别查询日志；fromDate / toDate 为 null 表示不限日期（扫描目录下全部日志文件）。
    /// 结果按时间倒序，最多返回 maxRows 条。
    /// </summary>
    public static List<ManualOperationLogEntry> Query(
        ManualOperationCategory category,
        DateTime? fromDate,
        DateTime? toDate,
        string? keyword,
        int maxRows = 20000)
    {
        var results = new List<ManualOperationLogEntry>();
        int remaining = Math.Max(1, maxRows);
        string? keywordTrim = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        string categoryKey = category.ToString();

        foreach ((DateTime day, string path) in EnumerateLogFiles(fromDate, toDate))
        {
            if (remaining <= 0)
                break;

            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Cache=Shared");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT log_time, category, device, target, sub_target, action, address,
                           before_value, after_value, detail, operator
                    FROM manual_operation_log
                    WHERE category = $category
                      AND ($keyword IS NULL
                           OR target LIKE $keywordLike
                           OR sub_target LIKE $keywordLike
                           OR action LIKE $keywordLike
                           OR address LIKE $keywordLike
                           OR detail LIKE $keywordLike)
                    ORDER BY log_time DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$category", categoryKey);
                command.Parameters.AddWithValue("$keyword", keywordTrim ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$keywordLike", keywordTrim is null ? DBNull.Value : $"%{keywordTrim}%");
                command.Parameters.AddWithValue("$limit", remaining);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!TryParseTime(reader.GetString(0), out DateTime logTime))
                        continue;

                    results.Add(new ManualOperationLogEntry
                    {
                        LogTime = logTime,
                        Category = category,
                        Device = reader.GetString(2),
                        Target = reader.GetString(3),
                        SubTarget = reader.GetString(4),
                        Action = reader.GetString(5),
                        Address = reader.GetString(6),
                        BeforeValue = reader.GetString(7),
                        AfterValue = reader.GetString(8),
                        Detail = reader.GetString(9),
                        Operator = reader.GetString(10)
                    });

                    if (--remaining <= 0)
                        break;
                }
            }
            catch
            {
                // 跳过损坏或结构不符的日志库
            }
        }

        results.Sort((a, b) => b.LogTime.CompareTo(a.LogTime));
        if (results.Count > maxRows)
            results.RemoveRange(maxRows, results.Count - maxRows);

        return results;
    }

    /// <summary>列出指定日期区间内实际存在的日志文件（新日期在前）。</summary>
    private static IEnumerable<(DateTime Date, string Path)> EnumerateLogFiles(DateTime? fromDate, DateTime? toDate)
    {
        if (fromDate.HasValue && toDate.HasValue)
        {
            DateTime from = fromDate.Value.Date;
            DateTime to = toDate.Value.Date;
            if (to < from)
                (from, to) = (to, from);

            for (DateTime day = to; day >= from; day = day.AddDays(-1))
            {
                string path = GetDatabasePath(day);
                if (File.Exists(path))
                    yield return (day, path);
            }

            yield break;
        }

        string[] files;
        try
        {
            if (!Directory.Exists(LogsDirectory))
                yield break;

            files = Directory.GetFiles(LogsDirectory, $"{FilePrefix}*.db");
        }
        catch
        {
            yield break;
        }

        var parsed = new List<(DateTime Date, string Path)>();
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string dateText = name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                ? name[FilePrefix.Length..]
                : name;
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                parsed.Add((date, file));
        }

        foreach (var item in parsed.OrderByDescending(x => x.Date))
            yield return item;
    }

    private static string ResolveOperator()
    {
        try
        {
            string? byProvider = OperatorProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(byProvider))
                return byProvider.Trim();
        }
        catch
        {
            // 忽略操作人解析异常
        }

        return Environment.UserName;
    }

    private static string Trim(string? text) => text?.Trim() ?? string.Empty;

    private static bool TryParseTime(string text, out DateTime value)
    {
        if (DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;

        return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
