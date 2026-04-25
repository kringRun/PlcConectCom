using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

// 运行授权校验：机器码生成、签名验签与本地持久化
internal static class LicenseAuthorization
{
    private const string LicenseFileName = "license.key";
    private const string LegacySecretKey = "HB_HMI_LICENSE_SECRET_2026";
    private const string LegacySecretIv = "HB_HMI_LICENSE_IV_2026";
    // 客户端仅保存公钥用于验签，不保存私钥。
    // 请替换为你自己的 RSA 公钥（PEM, SubjectPublicKeyInfo）。
    private const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAr/jESGZBhOMnE9myFJr2
gbHGKQfwl5xguyMSuaUZmF5d/hIyYnwNHrd14ExiKsEa4649mUxfy+1Wmf4Qbagt
LiIYNokhDcKtDaO8tyU/gB8sz46i4VB1rTsoekItWmA92VwQOdvlrkROqvatXPmE
NkmKgMM4ZISfZOsEC1GyVPQlFB9b802jUU2egZfTfRZEA/STOvqQO6iIFTrKBUyI
K7UOVQp9KoyMREpue5CHnZ6H/9rwz7prmccPCS6f0BBhtevD9WDWUaZTPHVQJMnY
TKD/LBJpWEGyfSN99dkJOFXlJTNtkaY5grO465L+wyjDKB8OpIaSTrEW0o+hRqQs
oQIDAQAB
-----END PUBLIC KEY-----
""";

    public static bool IsCurrentlyAuthorized(out DateTime expireAt, out string? error)
    {
        expireAt = DateTime.MinValue;
        error = null;

        string? localCode = LoadLocalAuthCode();
        if (string.IsNullOrWhiteSpace(localCode))
        {
            error = "未找到本地授权信息。";
            return false;
        }

        return TryValidateAuthCode(localCode, out expireAt, out error);
    }

    public static bool EnsureAuthorized(IWin32Window? owner)
    {
        if (IsCurrentlyAuthorized(out _, out _))
        {
            return true;
        }

        string? localCode = LoadLocalAuthCode();
        using var dialog = new LicenseAuthDialog(GetMachineCode(), localCode ?? string.Empty);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return false;

        if (!TryValidateAuthCode(dialog.AuthCode.Trim(), out DateTime exp, out string? error))
        {
            MessageBox.Show(owner, error ?? "授权码无效。", "授权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (exp < DateTime.Now)
        {
            MessageBox.Show(owner, $"授权已过期：{exp:yyyy-MM-dd HH:mm:ss}", "授权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        SaveLocalAuthCode(dialog.AuthCode.Trim());
        return true;
    }

    private static string? LoadLocalAuthCode()
    {
        string newPath = GetLicenseFilePath();
        if (File.Exists(newPath))
            return File.ReadAllText(newPath).Trim();

        string legacyPath = Path.Combine(AppContext.BaseDirectory, LicenseFileName);
        if (File.Exists(legacyPath))
            return File.ReadAllText(legacyPath).Trim();

        return null;
    }

    private static void SaveLocalAuthCode(string code)
    {
        string path = GetLicenseFilePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, code);
    }

    private static string GetLicenseFilePath()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "PlcConectCom", LicenseFileName);
    }

    private static string GetCpuSerial() => QueryWmiFirstValue("Win32_Processor", "ProcessorId");

    private static string GetBaseBoardSerial() => QueryWmiFirstValue("Win32_BaseBoard", "SerialNumber");

    private static string GetBiosSerial() => QueryWmiFirstValue("Win32_BIOS", "SerialNumber");

    private static string GetSystemDriveSerial()
    {
        try
        {
            string drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            string query = $"SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='{drive.Replace("\\", "\\\\")}'";
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject obj in searcher.Get())
            {
                string value = Convert.ToString(obj["VolumeSerialNumber"])?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // ignore and fallback
        }

        return string.Empty;
    }

    private static string GetMachineGuid()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null);
            string machineGuid = Convert.ToString(value)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(machineGuid))
                return machineGuid;
        }
        catch
        {
            // ignore and fallback
        }

        return string.Empty;
    }

    private static string QueryWmiFirstValue(string wmiClass, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {wmiClass}");
            foreach (ManagementObject obj in searcher.Get())
            {
                string value = Convert.ToString(obj[propertyName])?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // ignore and fallback
        }

        return string.Empty;
    }

    public static string GetMachineCode()
    {
        var parts = new[]
        {
            $"CPU={GetCpuSerial()}",
            $"BASEBOARD={GetBaseBoardSerial()}",
            $"BIOS={GetBiosSerial()}",
            $"SYSDRV={GetSystemDriveSerial()}",
            $"GUID={GetMachineGuid()}",
            $"NAME={Environment.MachineName}"
        };

        string fingerprint = string.Join("|", parts).ToUpperInvariant();
        return ComputeSha256Hex(fingerprint).ToUpperInvariant();
    }

    // 供离线授权工具使用：使用私钥签发授权码（不要在客户端调用）
    public static string GenerateAuthCode(string machineCode, DateTime expireAt, string privateKeyPem)
    {
        string payload = BuildPayload(machineCode, expireAt);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signatureBytes;

        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(privateKeyPem);
            signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return $"{Convert.ToBase64String(payloadBytes)}.{Convert.ToBase64String(signatureBytes)}";
    }

    private static bool TryValidateAuthCode(string authCode, out DateTime expireAt, out string? error)
    {
        expireAt = DateTime.MinValue;
        error = null;

        if (string.IsNullOrWhiteSpace(authCode))
        {
            error = "授权码为空。";
            return false;
        }

        if (!TryParseAndVerify(authCode, out string cpuCode, out string expText, out error))
        {
            // 兼容旧版授权码（AES加密格式），便于平滑迁移。
            if (!TryParseLegacyAuthCode(authCode, out cpuCode, out expText, out error))
            {
                return false;
            }
        }

        if (!string.Equals(cpuCode, GetMachineCode(), StringComparison.OrdinalIgnoreCase))
        {
            error = "授权码与当前机器不匹配。";
            return false;
        }

        if (!DateTime.TryParseExact(expText, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out expireAt))
        {
            error = "授权码到期时间格式错误。";
            return false;
        }

        if (expireAt < DateTime.Now)
        {
            error = $"授权已过期：{expireAt:yyyy-MM-dd HH:mm:ss}";
            return false;
        }

        return true;
    }

    private static bool TryParseLegacyAuthCode(string authCode, out string cpuCode, out string expText, out string? error)
    {
        cpuCode = string.Empty;
        expText = string.Empty;
        error = null;

        string plain;
        try
        {
            plain = DecryptLegacy(authCode);
        }
        catch
        {
            error = "授权码无效。";
            return false;
        }

        string[] parts = plain.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string cpuPart = parts.FirstOrDefault(x => x.StartsWith("CPU=", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        string expPart = parts.FirstOrDefault(x => x.StartsWith("EXP=", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cpuPart) || string.IsNullOrWhiteSpace(expPart))
        {
            error = "授权码内容格式错误。";
            return false;
        }

        cpuCode = cpuPart.Replace("CPU=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        expText = expPart.Replace("EXP=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return true;
    }

    private static bool TryParseAndVerify(string authCode, out string cpuCode, out string expText, out string? error)
    {
        cpuCode = string.Empty;
        expText = string.Empty;
        error = null;

        string[] segments = authCode.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2)
        {
            error = "授权码格式错误。";
            return false;
        }

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(segments[0]);
            signatureBytes = Convert.FromBase64String(segments[1]);
        }
        catch
        {
            error = "授权码Base64内容无效。";
            return false;
        }

        bool verified;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);
            verified = rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            error = "授权公钥配置无效。";
            return false;
        }

        if (!verified)
        {
            error = "授权码签名校验失败。";
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadBytes);
            JsonElement root = doc.RootElement;

            int ver = root.GetProperty("ver").GetInt32();
            if (ver != 2)
            {
                error = "授权版本不支持。";
                return false;
            }

            cpuCode = root.GetProperty("cpu").GetString()?.Trim() ?? string.Empty;
            expText = root.GetProperty("exp").GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cpuCode) || string.IsNullOrWhiteSpace(expText))
            {
                error = "授权码内容格式错误。";
                return false;
            }
        }
        catch
        {
            error = "授权码载荷解析失败。";
            return false;
        }

        return true;
    }

    private static string BuildPayload(string machineCode, DateTime expireAt)
    {
        string cpu = machineCode.Trim().ToUpperInvariant();
        string exp = expireAt.ToString("yyyyMMddHHmmss");
        return $"{{\"ver\":2,\"cpu\":\"{cpu}\",\"exp\":\"{exp}\"}}";
    }

    private static string DecryptLegacy(string cipherTextBase64)
    {
        byte[] cipherBytes = Convert.FromBase64String(cipherTextBase64);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(LegacySecretKey));
        aes.IV = MD5.HashData(Encoding.UTF8.GetBytes(LegacySecretIv));

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string ComputeSha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private sealed class LicenseAuthDialog : Form
    {
        private readonly TextBox _txtAuthCode = new() { Dock = DockStyle.Fill };

        public LicenseAuthDialog(string machineCode, string authCode)
        {
            Text = "授权验证";
            Width = 560;
            Height = 250;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var txtMachine = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = machineCode };
            _txtAuthCode.Text = authCode;

            layout.Controls.Add(new Label { Text = "机器码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(txtMachine, 1, 0);
            layout.Controls.Add(new Label { Text = "授权码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(_txtAuthCode, 1, 1);
            layout.Controls.Add(new Label { Text = "请联系供应商根据机器码生成授权码。", AutoSize = true, ForeColor = Color.DimGray }, 1, 2);

            var panelBtn = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            var btnOk = new Button { Text = "确认", Width = 72, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "退出", Width = 72, DialogResult = DialogResult.Cancel };
            panelBtn.Controls.Add(btnOk);
            panelBtn.Controls.Add(btnCancel);

            Controls.Add(layout);
            Controls.Add(panelBtn);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        public string AuthCode => _txtAuthCode.Text;
    }
}
