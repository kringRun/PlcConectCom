using System.Globalization;

namespace PlcConectCom.WinFormsTest;

/// <summary>Modbus 服务端地址区显示的制式。</summary>
public enum ModbusAddressNotation
{
    /// <summary>沿用项目私有制式：MX（位）/ MB（字节）/ MW（字）/ MD（双字）。</summary>
    私有地址,

    /// <summary>标准 Modbus：保持寄存器 40001 起、线圈 00001 起。</summary>
    标准Modbus
}

/// <summary>
/// Modbus 服务端地址区映射：内部统一按「保持寄存器索引 / 线性位索引」存储，
/// 界面上可在私有制式（MX/MB/MW/MD）与标准 Modbus（40001 / 00001）之间切换显示与输入。
/// 与 PLC 侧一致：MW{n} ↔ 寄存器 n（1:1）；MD{n} ↔ 寄存器 2n；MB{n} ↔ 寄存器 n/2；MX{b/8}.{b%8} ↔ 线性位 b。
/// </summary>
public static class ModbusServerAddressMap
{
    public static string RegisterToMw(int register) => "MW" + register.ToString(CultureInfo.InvariantCulture);

    public static string RegisterToMb(int register) => "MB" + (register * 2).ToString(CultureInfo.InvariantCulture);

    public static string RegisterToMd(int register) => "MD" + (register / 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>寄存器内第 bit 位（0..15，低字节在前）对应的 MX 地址。</summary>
    public static string RegisterBitToMx(int register, int bit) => $"MX{register * 2 + bit / 8}.{bit % 8}";

    /// <summary>线性位索引（线圈区）对应的 MX 地址。</summary>
    public static string BitToMx(int bit) => $"MX{bit / 8}.{bit % 8}";

    /// <summary>线性位索引对应的 PLC 侧批量读写起始地址（供 ReadBoolArrayAsync 使用）。</summary>
    public static string BitToMxStart(int bit) => $"MX{bit / 8}.{bit % 8}";

    public static string RegisterToStandard(int register) => (40001 + register).ToString(CultureInfo.InvariantCulture);

    public static string BitToStandard(int bit) => (1 + bit).ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>按当前制式生成地址区浏览表格里的地址文本。</summary>
    public static string FormatRegisterAddress(int register, ModbusAddressNotation notation)
        => notation == ModbusAddressNotation.标准Modbus
            ? RegisterToStandard(register)
            : RegisterToMw(register);

    public static string FormatBitAddress(int bit, ModbusAddressNotation notation)
        => notation == ModbusAddressNotation.标准Modbus
            ? BitToStandard(bit)
            : BitToMx(bit);

    /// <summary>
    /// 解析界面输入的起始地址（两种制式都接受）：
    /// MW123 / MB200 / MD400 / MX100.3 / 40001 / 00001 / 123。
    /// 返回线性索引与是否为位（线圈）区。
    /// </summary>
    public static bool TryParseStartAddress(
        string? text,
        out bool isBit,
        out int index,
        out string? error)
    {
        isBit = false;
        index = 0;
        error = null;

        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            error = "请输入起始地址。";
            return false;
        }

        string upper = s.ToUpperInvariant();
        if (upper.StartsWith("MW", StringComparison.Ordinal)
            && int.TryParse(upper[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mw))
        {
            index = mw;
            return true;
        }

        if (upper.StartsWith("MB", StringComparison.Ordinal)
            && int.TryParse(upper[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb))
        {
            index = mb / 2;
            return true;
        }

        if (upper.StartsWith("MD", StringComparison.Ordinal)
            && int.TryParse(upper[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int md))
        {
            index = md * 2;
            return true;
        }

        if (upper.StartsWith("MX", StringComparison.Ordinal))
        {
            string[] parts = upper[2..].Trim().Split('.');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int byteOffset)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit)
                && bit is >= 0 and <= 7)
            {
                isBit = true;
                index = byteOffset * 8 + bit;
                return true;
            }

            error = "MX 地址格式应为 MX[字节号].[位号]，如 MX100.3。";
            return false;
        }

        if (upper.StartsWith("4", StringComparison.Ordinal)
            && int.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out int std4)
            && std4 >= 40001)
        {
            index = std4 - 40001;
            return true;
        }

        if (upper.StartsWith("0", StringComparison.Ordinal)
            && upper.Length >= 5
            && int.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out int std0))
        {
            isBit = true;
            index = std0 - 1;
            return true;
        }

        if (int.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain))
        {
            index = plain;
            return true;
        }

        error = $"无法识别的地址[{s}]；支持 MW/MB/MD/MX 或 40001 / 00001 或纯寄存器号。";
        return false;
    }
}
