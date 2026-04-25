namespace PlcConectCom.WinFormsTest;

partial class MonitorPageWindow
{
    private System.ComponentModel.IContainer components = null;
    private Panel _contentHost;
    private Label _previewTitle;
    private DataGridView _previewGrid;
    private FlowLayoutPanel _previewToolbar;
    private Button _btnRefresh;
    private Button _btnUnlock;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        _contentHost = new Panel();
        _previewTitle = new Label();
        _previewGrid = new DataGridView();
        _previewToolbar = new FlowLayoutPanel();
        _btnRefresh = new Button();
        _btnUnlock = new Button();
        ((System.ComponentModel.ISupportInitialize)_previewGrid).BeginInit();
        _previewToolbar.SuspendLayout();
        _contentHost.SuspendLayout();
        SuspendLayout();
        _contentHost.BackColor = Color.White;
        _contentHost.Controls.Add(_previewGrid);
        _contentHost.Controls.Add(_previewToolbar);
        _contentHost.Controls.Add(_previewTitle);
        _contentHost.Dock = DockStyle.Fill;
        _contentHost.Location = new Point(0, 35);
        _contentHost.Name = "_contentHost";
        _contentHost.Size = new Size(1400, 865);
        _contentHost.TabIndex = 0;
        _previewTitle.Dock = DockStyle.Top;
        _previewTitle.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _previewTitle.Location = new Point(0, 0);
        _previewTitle.Name = "_previewTitle";
        _previewTitle.Padding = new Padding(12, 0, 0, 0);
        _previewTitle.Size = new Size(1400, 40);
        _previewTitle.TabIndex = 0;
        _previewTitle.Text = "状态监控页面";
        _previewTitle.TextAlign = ContentAlignment.MiddleLeft;
        _previewToolbar.Controls.Add(_btnRefresh);
        _previewToolbar.Controls.Add(_btnUnlock);
        _previewToolbar.Dock = DockStyle.Top;
        _previewToolbar.Location = new Point(0, 40);
        _previewToolbar.Name = "_previewToolbar";
        _previewToolbar.Padding = new Padding(12, 6, 0, 6);
        _previewToolbar.Size = new Size(1400, 44);
        _previewToolbar.TabIndex = 1;
        _previewToolbar.WrapContents = false;
        _btnRefresh.Text = "刷新状态";
        _btnRefresh.Width = 90;
        _btnUnlock.Text = "编辑解锁";
        _btnUnlock.Width = 90;
        _previewGrid.AllowUserToAddRows = false;
        _previewGrid.AllowUserToDeleteRows = false;
        _previewGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _previewGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _previewGrid.Dock = DockStyle.Fill;
        _previewGrid.Location = new Point(0, 84);
        _previewGrid.Name = "_previewGrid";
        _previewGrid.ReadOnly = true;
        _previewGrid.RowHeadersVisible = false;
        _previewGrid.Size = new Size(1400, 781);
        _previewGrid.TabIndex = 2;
        _previewGrid.Columns.Add("Name", "信号名称");
        _previewGrid.Columns.Add("Address", "地址");
        _previewGrid.Columns.Add("CurrentValue", "当前值");
        _previewGrid.Columns.Add("Status", "状态");
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(1400, 900);
        Controls.Add(_contentHost);
        MaximizeBox = true;
        MinimizeBox = true;
        Name = "MonitorPageWindow";
        Text = "状态监控";
        ((System.ComponentModel.ISupportInitialize)_previewGrid).EndInit();
        _previewToolbar.ResumeLayout(false);
        _contentHost.ResumeLayout(false);
        ResumeLayout(false);
    }
}
