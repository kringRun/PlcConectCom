using System.Text.RegularExpressions;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 从 Excel「Comment / 注释」列解析轴组 PLC 字段，并生成供轴索引名推断使用的前缀注释。
/// 约定格式示例：<c>0: A_X 1: A_Y … 轴组运动启动地址: MB21000;轴组点位编号地址: MB21001;轴组点位编号: 1</c>
/// </summary>
public static class ExcelPositionCommentAxisGroupParser
{
    private static readonly Regex MotionStartRegex = new(
        @"轴组运动启动地址\s*[:：]\s*(?<v>[^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PointNumberAddrRegex = new(
        @"轴组点位编号地址\s*[:：]\s*(?<v>[^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>「轴组点位编号:」且非「轴组点位编号地址」。</summary>
    private static readonly Regex PointNumberDataRegex = new(
        @"轴组点位编号(?!地址)\s*[:：]\s*(?<v>[^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] AxisGroupMarkers =
    [
        "轴组运动启动地址",
        "轴组点位编号地址",
        "轴组点位编号:",
        "轴组点位编号："
    ];

    /// <summary>去掉尾部「轴组…」描述，仅保留 <c>0: 轴名 …</c> 段，供索引轴名正则匹配。</summary>
    public static string CommentPrefixForIndexedAxisNames(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return comment;

        int min = -1;
        foreach (string marker in AxisGroupMarkers)
        {
            int i = comment.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && (min < 0 || i < min))
                min = i;
        }

        if (min < 0)
            return comment;

        return comment[..min].TrimEnd();
    }

    /// <summary>
    /// 解析：触发定位 PLC（轴组运动启动地址）、点位编号 PLC（轴组点位编号地址）、点位编号数据（轴组点位编号）。
    /// </summary>
    public static (string LocateTriggerPlcAddress, string PointNumberPlcAddress, string PointNumberData) ParseAxisGroupPlcFields(
        string comment)
    {
        string trigger = string.Empty;
        string pointAddr = string.Empty;
        string pointData = string.Empty;

        if (string.IsNullOrWhiteSpace(comment))
            return (trigger, pointAddr, pointData);

        foreach (string raw in comment.Split(';'))
        {
            string seg = raw.Trim();
            if (seg.Length == 0)
                continue;

            if (seg.StartsWith("轴组运动启动地址", StringComparison.Ordinal))
                trigger = NormalizePlcToken(ExtractAfterKey(seg, "轴组运动启动地址"));
            else if (seg.StartsWith("轴组点位编号地址", StringComparison.Ordinal))
                pointAddr = NormalizePlcToken(ExtractAfterKey(seg, "轴组点位编号地址"));
            else if (seg.StartsWith("轴组点位编号", StringComparison.Ordinal)
                     && !seg.StartsWith("轴组点位编号地址", StringComparison.Ordinal))
                pointData = ExtractAfterKey(seg, "轴组点位编号").Trim();
        }

        if (string.IsNullOrWhiteSpace(trigger))
        {
            Match m = MotionStartRegex.Match(comment);
            if (m.Success)
                trigger = NormalizePlcToken(m.Groups["v"].Value);
        }

        if (string.IsNullOrWhiteSpace(pointAddr))
        {
            Match m = PointNumberAddrRegex.Match(comment);
            if (m.Success)
                pointAddr = NormalizePlcToken(m.Groups["v"].Value);
        }

        if (string.IsNullOrWhiteSpace(pointData))
        {
            Match m = PointNumberDataRegex.Match(comment);
            if (m.Success)
                pointData = m.Groups["v"].Value.Trim();
        }

        return (trigger, pointAddr, pointData);
    }

    private static string ExtractAfterKey(string segment, string key)
    {
        string rest = segment[key.Length..].Trim();
        if (rest.Length > 0 && (rest[0] == ':' || rest[0] == '：'))
            rest = rest[1..].Trim();

        return rest;
    }

    private static string NormalizePlcToken(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith('%'))
            s = s[1..];

        return s.Trim();
    }
}
