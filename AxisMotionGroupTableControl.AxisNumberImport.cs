using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDataReader;

namespace PlcConectCom.WinFormsTest;

public sealed partial class AxisMotionGroupTableControl
{
    private void ImportAxisNumbersFromExcel()
    {
        if (!_canAddDelete || !_canModify)
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "选择轴编号表（Name / InitValue）",
            Filter = "Excel 文件 (*.xls;*.xlsx)|*.xls;*.xlsx",
            InitialDirectory = AppContext.BaseDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var entries = ReadAxisNumberTable(dialog.FileName);
            var existing = GetAxes().GroupBy(x => x.AxisId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var configs = entries.Select(entry =>
            {
                existing.TryGetValue(entry.Name, out var previous);
                return new AxisMotionAddressConfig
                {
                    AxisId = entry.Name,
                    AxisNumber = entry.Number,
                    IsAbsEncorder = previous?.IsAbsEncorder ?? false,
                    Addresses = previous?.Addresses.ToDictionary(x => x.Key, x => x.Value) ?? []
                };
            }).ToList();

            if (_grid.Rows.Count > 0 && MessageBox.Show(this,
                $"将当前轴列表替换为表格中的 {configs.Count} 根轴，并按 InitValue 升序排列。\n同名轴保留地址配置，新轴需要配置地址。是否导入？",
                "导入轴编号", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            // 先成功落盘再替换界面；文件读取或保存失败时保持原轴列表。
            WriteAxisConfiguration(configs);
            SetAxes(configs);
            RebuildPanelAxisCombo(configs[0].AxisId);
            LogAction?.Invoke($"轴编号导入完成：{configs.Count} 根轴，已按 InitValue 升序保存到 {_configPath}。");
            MessageBox.Show(this, $"已导入并保存 {configs.Count} 根轴，按 InitValue 升序排列。",
                "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"轴编号导入失败: {ex.Message}");
            MessageBox.Show(this, $"导入失败：{ex.Message}", "导入轴编号",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static List<(string Name, int Number)> ReadAxisNumberTable(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            int rowNumber = 0;
            int nameColumn = -1;
            int numberColumn = -1;
            bool headerFound = false;
            var entries = new List<(string Name, int Number)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var numbers = new HashSet<int>();
            while (reader.Read())
            {
                rowNumber++;
                if (!headerFound)
                {
                    // 允许表头前存在标题行，且不要求两列位于固定位置。
                    nameColumn = numberColumn = -1;
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string heading = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                        if (heading.Equals("Name", StringComparison.OrdinalIgnoreCase)) nameColumn = i;
                        if (heading.Equals("InitValue", StringComparison.OrdinalIgnoreCase)) numberColumn = i;
                    }
                    headerFound = nameColumn >= 0 && numberColumn >= 0;
                    continue;
                }

                string name = Convert.ToString(reader.GetValue(nameColumn), CultureInfo.InvariantCulture)?.Trim() ?? "";
                string value = Convert.ToString(reader.GetValue(numberColumn), CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (name.Length == 0 && value.Length == 0)
                    continue;
                string location = $"工作表「{reader.Name}」第 {rowNumber} 行";
                if (name.Length == 0)
                    throw new InvalidDataException($"{location}的 Name 为空。");
                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                    || number < 0 || number > int.MaxValue || decimal.Truncate(number) != number)
                    throw new InvalidDataException($"{location}的 InitValue 必须为非负整数，当前值为「{value}」。");
                int axisNumber = (int)number;
                if (!names.Add(name))
                    throw new InvalidDataException($"{location}的轴名称「{name}」重复。");
                if (!numbers.Add(axisNumber))
                    throw new InvalidDataException($"{location}的轴编号 {axisNumber} 重复。");
                entries.Add((name, axisNumber));
            }
            if (headerFound)
            {
                if (entries.Count == 0)
                    throw new InvalidDataException("轴编号表没有有效数据行。");
                return entries.OrderBy(x => x.Number).ToList();
            }
        } while (reader.NextResult());
        throw new InvalidDataException("未找到同时包含 Name、InitValue 表头的工作表。");
    }

    private void WriteAxisConfiguration(IReadOnlyList<AxisMotionAddressConfig> configs)
    {
        string temporaryPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configs, JsonFileOptions));
            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
