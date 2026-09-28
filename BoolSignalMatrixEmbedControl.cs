using System.Drawing.Drawing2D;
using System.Text.Json;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 调试页「输出点位」右侧：根据 BoolSignalCollection.config.json 显示输入信号点阵（与状态监控「点阵显示」布局一致）。
/// </summary>
public sealed class BoolSignalMatrixEmbedControl : UserControl
{
    private const string DefaultConfigFileName = "BoolSignalCollection.config.json";

    private readonly Label _lblTitle = new()
    {
        Text = "输入信号点阵",
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 6, 6, 2),
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        BackColor = Color.WhiteSmoke
    };

    private readonly Panel _scrollHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly List<(string name, string address, Panel light)> _matrixItems = [];
    private readonly List<(TableLayoutPanel Tile, string Name)> _matrixTiles = [];
    private readonly ToolTip _nameTips = new() { ShowAlways = true };
    private TableLayoutPanel? _matrix;
    private int _preferredItemWidth;
    private int _arrangedWidth = -1;
    private int _matrixVersion;
    private bool _arranging;
    private System.Windows.Forms.Timer? _refreshTimer;
    private bool _refreshing;
    private readonly string _configFilePath;

    public BoolSignalMatrixEmbedControl() : this(null) { }

    public BoolSignalMatrixEmbedControl(string? storageKey)
    {
        string fileName = DefaultConfigFileName;
        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            string safeKey = string.Concat(storageKey.Where(char.IsLetterOrDigit));
            if (!string.IsNullOrWhiteSpace(safeKey))
                fileName = $"{Path.GetFileNameWithoutExtension(DefaultConfigFileName)}.{safeKey}{Path.GetExtension(DefaultConfigFileName)}";
        }
        _configFilePath = Path.Combine(AppContext.BaseDirectory, fileName);
        BackColor = Color.WhiteSmoke;
        Padding = new Padding(4);
        var inner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), BackColor = Color.WhiteSmoke };
        inner.Controls.Add(_scrollHost);
        inner.Controls.Add(_lblTitle);
        Controls.Add(inner);

        VisibleChanged += (_, _) => SyncTimer();
        HandleDestroyed += (_, _) => StopTimer();
        Load += (_, _) => ReloadFromConfigFile();
        _scrollHost.ClientSizeChanged += (_, _) => ArrangeMatrix();
    }

    public Func<global::PlcConectCom.PlcCommunicator?>? PlcProvider { get; set; }

    public global::PlcConectCom.PlcCommunicator? PlcInstance { get; set; }

    public Action<string>? LogAction { get; set; }

    /// <summary>从磁盘重新加载配置并重建点阵（状态监控保存后可调用以同步）。</summary>
    public void ReloadFromConfigFile()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                RebuildMatrix([]);
                return;
            }

            string json = File.ReadAllText(_configFilePath);
            var points = JsonSerializer.Deserialize<List<BoolSignalPoint>>(json);
            RebuildMatrix(points ?? []);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"加载输入点阵配置失败: {ex.Message}");
            RebuildMatrix([]);
        }
    }

    private void RebuildMatrix(IReadOnlyList<BoolSignalPoint> points)
    {
        StopTimer();
        _matrixVersion++;
        _matrixItems.Clear();
        _matrixTiles.Clear();
        _nameTips.RemoveAll();
        _matrix = null;
        _arrangedWidth = -1;
        foreach (Control old in _scrollHost.Controls.Cast<Control>().ToArray())
        {
            _scrollHost.Controls.Remove(old);
            old.Dispose();
        }
        _scrollHost.AutoScrollMinSize = Size.Empty;
        _scrollHost.AutoScrollPosition = Point.Empty;

        var list = points
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) || !string.IsNullOrWhiteSpace(p.Address))
            .ToList();

        if (list.Count == 0)
        {
            var hint = new Label
            {
                Text = "暂无输入点配置；请在「状态监控」维护 BOOL 信号表并保存。",
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(Math.Max(180, _scrollHost.ClientSize.Width), 0),
                Padding = new Padding(8),
                ForeColor = Color.DimGray
            };
            _scrollHost.Controls.Add(hint);
            return;
        }

        Font nameFont = Font;
        int maxNameWidth = list
            .Select(BuildMatrixDisplayName)
            .DefaultIfEmpty(string.Empty)
            .Max(name => TextRenderer.MeasureText(name, nameFont).Width);
        _preferredItemWidth = Math.Clamp(maxNameWidth + 44, 240, 480);

        var matrix = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            AutoSize = false,
            Padding = new Padding(8),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = Color.White
        };

        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Height = 32,
                ColumnCount = 2,
                Margin = new Padding(2),
                BackColor = Color.White
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var light = CreateCircleIndicator(Color.DarkGray);
            var lblName = new Label
            {
                Text = BuildMatrixDisplayName(p),
                AutoSize = false,
                UseMnemonic = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = nameFont,
                Padding = new Padding(0, 3, 0, 0)
            };

            row.Controls.Add(light, 0, 0);
            row.Controls.Add(lblName, 1, 0);
            _nameTips.SetToolTip(lblName, lblName.Text);
            _matrixTiles.Add((row, lblName.Text));
            _matrixItems.Add((p.Name, p.Address, light));
        }

        _matrix = matrix;
        _scrollHost.Controls.Add(matrix);
        ArrangeMatrix();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _refreshTimer.Tick += OnRefreshTick;
        SyncTimer();

        if (IsHandleCreated && Visible)
            _ = RefreshMatrixStatusAsync();
    }

    private void ArrangeMatrix()
    {
        var matrix = _matrix;
        if (_arranging || matrix is null || matrix.IsDisposed || _matrixTiles.Count == 0
            || _scrollHost.ClientSize.Width < 60)
            return;

        int width = Math.Max(40, _scrollHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        if (width == _arrangedWidth) return;
        _arranging = true;
        matrix.SuspendLayout();
        try
        {
            int innerWidth = Math.Max(1, width - matrix.Padding.Horizontal);
            int columns = Math.Max(1, Math.Min(_matrixTiles.Count, innerWidth / _preferredItemWidth));
            int rows = (int)Math.Ceiling(_matrixTiles.Count / (double)columns);
            int cellWidth = Math.Max(1, innerWidth / columns);
            var rowHeights = Enumerable.Repeat(34, rows).ToArray();
            for (int i = 0; i < _matrixTiles.Count; i++)
            {
                int textHeight = TextRenderer.MeasureText(_matrixTiles[i].Name, Font,
                    new Size(Math.Max(1, cellWidth - 44), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                rowHeights[i % rows] = Math.Max(rowHeights[i % rows], textHeight + 14);
            }

            matrix.Controls.Clear();
            matrix.ColumnStyles.Clear();
            matrix.RowStyles.Clear();
            matrix.ColumnCount = columns;
            matrix.RowCount = rows;
            for (int c = 0; c < columns; c++)
                matrix.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, cellWidth));
            foreach (int height in rowHeights)
                matrix.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            for (int i = 0; i < _matrixTiles.Count; i++)
                matrix.Controls.Add(_matrixTiles[i].Tile, i / rows, i % rows);

            int heightTotal = matrix.Padding.Vertical + rowHeights.Sum();
            matrix.Size = new Size(width, heightTotal);
            _scrollHost.AutoScrollMinSize = new Size(0, heightTotal);
            _arrangedWidth = width;
        }
        finally
        {
            matrix.ResumeLayout(true);
            _arranging = false;
        }
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (_matrixItems.Count == 0 || !Visible)
            return;

        _ = RefreshMatrixStatusAsync();
    }

    private void SyncTimer()
    {
        if (_refreshTimer is null || !IsHandleCreated)
            return;

        if (Visible && _matrixItems.Count > 0)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void StopTimer()
    {
        if (_refreshTimer is null)
            return;

        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Dispose();
        _refreshTimer = null;
    }

    private async Task RefreshMatrixStatusAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        int version = _matrixVersion;
        var items = _matrixItems.ToArray();
        try
        {
            var plc = PlcProvider?.Invoke() ?? PlcInstance;
            if (plc is not { IsConnected: true })
            {
                foreach (var x in items)
                {
                    if (IsDisposed || version != _matrixVersion || x.light.IsDisposed) return;
                    x.light.BackColor = Color.DarkGray;
                }
                return;
            }

            foreach (var x in items)
            {
                if (IsDisposed || version != _matrixVersion || x.light.IsDisposed) return;
                string addr = x.address.Trim();
                if (!IsSupportedAddress(addr))
                {
                    x.light.BackColor = Color.DarkGray;
                    continue;
                }

                try
                {
                    bool value = await plc.ReadBoolAsync(addr);
                    if (IsDisposed || version != _matrixVersion || x.light.IsDisposed) return;
                    x.light.BackColor = value ? Color.LimeGreen : Color.Red;
                }
                catch
                {
                    if (IsDisposed || version != _matrixVersion || x.light.IsDisposed) return;
                    x.light.BackColor = Color.DarkOrange;
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _matrixVersion++;
            StopTimer();
            _nameTips.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Panel CreateCircleIndicator(Color color)
    {
        var panel = new Panel { Width = 16, Height = 16, BackColor = color, Margin = new Padding(4, 6, 4, 4) };
        panel.Resize += (_, _) =>
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, panel.Width - 1, panel.Height - 1);
            panel.Region = new Region(path);
        };
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(0, 0, panel.Width - 1, panel.Height - 1);
            panel.Region = new Region(path);
        }

        return panel;
    }

    private static bool IsSupportedAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            var (type, _, _) = global::PlcConectCom.AddressParser.ParseAddress(address);
            return type is "MX" or "MB";
        }
        catch
        {
            return false;
        }
    }

    private static string BuildMatrixDisplayName(BoolSignalPoint point)
    {
        string name = point.Name?.Trim() ?? string.Empty;
        string comment = point.Comment?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(comment))
            return $"{name}_{comment}";
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        if (!string.IsNullOrWhiteSpace(comment))
            return comment;
        return "(未命名)";
    }
}
