using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 报警轮询用的布尔批量读取辅助：
/// 把一批 MX/MB 地址按「相邻间隔小、总跨度不超单次 Modbus 上限」聚合成少数几个连续块，
/// 每块一次寄存器批量读（ReadBoolArrayAsync / ReadByteArrayAsync），块内读取失败时逐条回退。
/// 相比逐条 ReadBoolAsync，N 条报警从 N 次往返降到个位数往返，消除报警显示滞后。
/// </summary>
internal static class PlcBoolBatchReader
{
    // Modbus FC03 单次最多读 125 个寄存器，留余量
    private const int MaxRegistersPerRead = 120;
    private const int MaxBitsPerChunk = MaxRegistersPerRead * 8;
    private const int MaxBytesPerChunk = MaxRegistersPerRead * 2;

    // 相邻地址间隔超过该值就另起一块，避免为了几个离散地址读一大段无关寄存器
    private const int MxGapBits = 32;
    private const int MbGapBytes = 16;

    /// <summary>
    /// 批量读取一批 MX/MB 布尔地址。
    /// 返回「原始地址字符串(已去空白) → 状态」；读取失败或地址非法的条目不会出现在结果里，由调用方按缺省处理。
    /// </summary>
    public static async Task<Dictionary<string, bool>> ReadBoolStatesAsync(
        global::PlcConectCom.PlcCommunicator plc,
        IEnumerable<string> addresses)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (plc is null || !plc.IsConnected)
            return result;

        var mxItems = new List<(string Address, int AbsBit)>();
        var mbItems = new List<(string Address, int ByteOffset)>();
        foreach (string raw in addresses)
        {
            string address = raw?.Trim();
            if (string.IsNullOrWhiteSpace(address) || result.ContainsKey(address))
                continue;

            try
            {
                var (type, offset, bit) = global::PlcConectCom.AddressParser.ParseAddress(address);
                if (type == "MX")
                    mxItems.Add((address, offset * 8 + bit));
                else if (type == "MB")
                    mbItems.Add((address, offset));
            }
            catch
            {
                // 非法地址不进结果，调用方按缺省处理
            }
        }

        foreach (var chunk in BuildChunks(mxItems, x => x.AbsBit, MxGapBits, MaxBitsPerChunk))
        {
            int start = chunk[0].AbsBit;
            int count = chunk[^1].AbsBit - start + 1;
            try
            {
                bool[] values = await plc.ReadBoolArrayAsync($"MX{start / 8}.{start % 8}", count).ConfigureAwait(true);
                foreach (var item in chunk)
                {
                    int index = item.AbsBit - start;
                    result[item.Address] = index < values.Length && values[index];
                }
            }
            catch
            {
                // 块读取失败（如跨度内有未映射寄存器）时逐条回退，保证不影响其他块
                foreach (var item in chunk)
                {
                    try
                    {
                        result[item.Address] = await plc.ReadBoolAsync(item.Address).ConfigureAwait(true);
                    }
                    catch
                    {
                        // 留空，调用方按 ERR/缺省处理
                    }
                }
            }
        }

        foreach (var chunk in BuildChunks(mbItems, x => x.ByteOffset, MbGapBytes, MaxBytesPerChunk))
        {
            int start = chunk[0].ByteOffset;
            int count = chunk[^1].ByteOffset - start + 1;
            try
            {
                byte[] values = await plc.ReadByteArrayAsync($"MB{start}", count).ConfigureAwait(true);
                foreach (var item in chunk)
                {
                    int index = item.ByteOffset - start;
                    result[item.Address] = index < values.Length && values[index] != 0;
                }
            }
            catch
            {
                foreach (var item in chunk)
                {
                    try
                    {
                        result[item.Address] = await plc.ReadBoolAsync(item.Address).ConfigureAwait(true);
                    }
                    catch
                    {
                        // 留空，调用方按 ERR/缺省处理
                    }
                }
            }
        }

        return result;
    }

    /// <summary>按 key 排序后切块：相邻间隔超过 maxGap 或块跨度超过 maxSpan 时断开。</summary>
    private static List<List<T>> BuildChunks<T>(IEnumerable<T> items, Func<T, int> key, int maxGap, int maxSpan)
    {
        var chunks = new List<List<T>>();
        List<T>? current = null;
        int chunkStart = 0;

        foreach (T item in items.OrderBy(key))
        {
            int k = key(item);
            if (current is null || k - key(current[^1]) > maxGap || k - chunkStart > maxSpan)
            {
                if (current is not null)
                    chunks.Add(current);
                current = [item];
                chunkStart = k;
            }
            else
            {
                current.Add(item);
            }
        }

        if (current is not null)
            chunks.Add(current);
        return chunks;
    }
}
