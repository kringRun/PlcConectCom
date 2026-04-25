using System.Globalization;

namespace PlcConectCom.WinFormsTest;

/// <summary>按通讯实例配置，将 Socket 回包文本拆成数组并按「结果字段标记」取值。</summary>
public static class SocketResponseParser
{
    /// <summary>多路写入字典中表示「整段回包原文」的键。</summary>
    public const string FullPayloadMapKey = "__full__";

    public static string[] SplitValues(string raw, string? delimiter)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        string d = string.IsNullOrWhiteSpace(delimiter) ? "," : delimiter!;
        return raw.Split(d, StringSplitOptions.None)
            .Select(s => s.Trim())
            .ToArray();
    }

    public static bool TryGetValueByMarkerName(
        IReadOnlyList<SocketClientResultField>? markers,
        IReadOnlyList<string> parts,
        string? markerName,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(markerName) || markers is null || markers.Count == 0)
            return false;

        SocketClientResultField? m = markers.FirstOrDefault(x =>
            string.Equals(x.Name, markerName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is null)
            return false;

        if (m.Index < 0 || m.Index >= parts.Count)
            return false;

        value = parts[m.Index] ?? string.Empty;
        return true;
    }

    /// <summary>markerName 为空时返回整段 raw（已 TrimEnd 换行）；否则按标记取分片，失败返回空串。</summary>
    public static string ResolveStoreValue(
        string raw,
        string? delimiter,
        IReadOnlyList<SocketClientResultField>? markers,
        string? markerName)
    {
        string trimmed = (raw ?? string.Empty).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(markerName))
            return trimmed;

        string[] parts = SplitValues(trimmed, delimiter);
        IReadOnlyList<string> partList = parts;
        return TryGetValueByMarkerName(markers, partList, markerName, out string v) ? v : string.Empty;
    }

    public static string FormatPartsForLog(IReadOnlyList<string> parts) =>
        parts.Count == 0 ? "0段" : $"{parts.Count}段:[{string.Join("|", parts)}]";
}
