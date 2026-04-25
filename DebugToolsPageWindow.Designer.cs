namespace PlcConectCom.WinFormsTest;

partial class DebugToolsPageWindow
{
    private System.ComponentModel.IContainer components = null;
    private Panel _contentHost;
    private TabControl _previewTab;
    private TabPage _tabAxisGroup;
    private TabPage _tabOutput;
    private SplitContainer _axisSplitPreview;
    private Label _axisTopTitle;
    private Label _axisBottomTitle;
    private DataGridView _outputGridPreview;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        _contentHost = new Panel();
        _previewTab = new TabControl();
        _tabAxisGroup = new TabPage();
        _axisSplitPreview = new SplitContainer();
        _axisTopTitle = new Label();
        _axisBottomTitle = new Label();
        _tabOutput = new TabPage();
        _outputGridPreview = new DataGridView();
        dataGridViewTextBoxColumn1 = new DataGridViewTextBoxColumn();
        dataGridViewTextBoxColumn2 = new DataGridViewTextBoxColumn();
        dataGridViewTextBoxColumn3 = new DataGridViewTextBoxColumn();
        dataGridViewTextBoxColumn4 = new DataGridViewTextBoxColumn();
        _btnOutputUnlock = new Button();
        _btnOutputImport = new Button();
        _outputToolbar = new FlowLayoutPanel();
        _contentHost.SuspendLayout();
        _previewTab.SuspendLayout();
        _tabAxisGroup.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_axisSplitPreview).BeginInit();
        _axisSplitPreview.Panel1.SuspendLayout();
        _axisSplitPreview.Panel2.SuspendLayout();
        _axisSplitPreview.SuspendLayout();
        _tabOutput.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_outputGridPreview).BeginInit();
        _outputToolbar.SuspendLayout();
        SuspendLayout();
        // 
        // _contentHost
        // 
        _contentHost.BackColor = Color.White;
        _contentHost.Controls.Add(_previewTab);
        _contentHost.Dock = DockStyle.Fill;
        _contentHost.Location = new Point(0, 35);
        _contentHost.Name = "_contentHost";
        _contentHost.Size = new Size(1400, 865);
        _contentHost.TabIndex = 0;
        // 
        // _previewTab
        // 
        _previewTab.Controls.Add(_tabAxisGroup);
        _previewTab.Controls.Add(_tabOutput);
        _previewTab.Dock = DockStyle.Fill;
        _previewTab.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
        _previewTab.Location = new Point(0, 0);
        _previewTab.Name = "_previewTab";
        _previewTab.SelectedIndex = 0;
        _previewTab.Size = new Size(1400, 865);
        _previewTab.TabIndex = 0;
        // 
        // _tabAxisGroup
        // 
        _tabAxisGroup.Controls.Add(_axisSplitPreview);
        _tabAxisGroup.Location = new Point(4, 28);
        _tabAxisGroup.Name = "_tabAxisGroup";
        _tabAxisGroup.Padding = new Padding(8);
        _tabAxisGroup.Size = new Size(1392, 833);
        _tabAxisGroup.TabIndex = 0;
        _tabAxisGroup.Text = "轴组控制";
        _tabAxisGroup.UseVisualStyleBackColor = true;
        // 
        // _axisSplitPreview
        // 
        _axisSplitPreview.Dock = DockStyle.Fill;
        _axisSplitPreview.Location = new Point(8, 8);
        _axisSplitPreview.Name = "_axisSplitPreview";
        _axisSplitPreview.Orientation = Orientation.Horizontal;
        // 
        // _axisSplitPreview.Panel1
        // 
        _axisSplitPreview.Panel1.Controls.Add(_axisTopTitle);
        // 
        // _axisSplitPreview.Panel2
        // 
        _axisSplitPreview.Panel2.Controls.Add(_axisBottomTitle);
        _axisSplitPreview.Size = new Size(1376, 817);
        _axisSplitPreview.SplitterDistance = 390;
        _axisSplitPreview.TabIndex = 0;
        // 
        // _axisTopTitle
        // 
        _axisTopTitle.Dock = DockStyle.Fill;
        _axisTopTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _axisTopTitle.Location = new Point(0, 0);
        _axisTopTitle.Name = "_axisTopTitle";
        _axisTopTitle.Padding = new Padding(12, 0, 0, 0);
        _axisTopTitle.Size = new Size(1376, 390);
        _axisTopTitle.TabIndex = 0;
        _axisTopTitle.Text = "轴组配置区域";
        _axisTopTitle.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _axisBottomTitle
        // 
        _axisBottomTitle.Dock = DockStyle.Fill;
        _axisBottomTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _axisBottomTitle.Location = new Point(0, 0);
        _axisBottomTitle.Name = "_axisBottomTitle";
        _axisBottomTitle.Padding = new Padding(12, 0, 0, 0);
        _axisBottomTitle.Size = new Size(1376, 423);
        _axisBottomTitle.TabIndex = 0;
        _axisBottomTitle.Text = "位置参数区域";
        _axisBottomTitle.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _tabOutput
        // 
        _tabOutput.Controls.Add(_outputGridPreview);
        _tabOutput.Controls.Add(_outputToolbar);
        _tabOutput.Location = new Point(4, 28);
        _tabOutput.Name = "_tabOutput";
        _tabOutput.Padding = new Padding(8);
        _tabOutput.Size = new Size(1392, 833);
        _tabOutput.TabIndex = 1;
        _tabOutput.Text = "输出点位";
        _tabOutput.UseVisualStyleBackColor = true;
        // 
        // _outputGridPreview
        // 
        _outputGridPreview.AllowUserToAddRows = false;
        _outputGridPreview.AllowUserToDeleteRows = false;
        _outputGridPreview.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _outputGridPreview.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _outputGridPreview.Columns.AddRange(new DataGridViewColumn[] { dataGridViewTextBoxColumn1, dataGridViewTextBoxColumn2, dataGridViewTextBoxColumn3, dataGridViewTextBoxColumn4 });
        _outputGridPreview.Dock = DockStyle.Fill;
        _outputGridPreview.Location = new Point(8, 48);
        _outputGridPreview.Name = "_outputGridPreview";
        _outputGridPreview.ReadOnly = true;
        _outputGridPreview.RowHeadersVisible = false;
        _outputGridPreview.Size = new Size(1376, 777);
        _outputGridPreview.TabIndex = 1;
        // 
        // dataGridViewTextBoxColumn1
        // 
        dataGridViewTextBoxColumn1.HeaderText = "名称";
        dataGridViewTextBoxColumn1.Name = "dataGridViewTextBoxColumn1";
        dataGridViewTextBoxColumn1.ReadOnly = true;
        // 
        // dataGridViewTextBoxColumn2
        // 
        dataGridViewTextBoxColumn2.HeaderText = "地址";
        dataGridViewTextBoxColumn2.Name = "dataGridViewTextBoxColumn2";
        dataGridViewTextBoxColumn2.ReadOnly = true;
        // 
        // dataGridViewTextBoxColumn3
        // 
        dataGridViewTextBoxColumn3.HeaderText = "按钮";
        dataGridViewTextBoxColumn3.Name = "dataGridViewTextBoxColumn3";
        dataGridViewTextBoxColumn3.ReadOnly = true;
        // 
        // dataGridViewTextBoxColumn4
        // 
        dataGridViewTextBoxColumn4.HeaderText = "值状态";
        dataGridViewTextBoxColumn4.Name = "dataGridViewTextBoxColumn4";
        dataGridViewTextBoxColumn4.ReadOnly = true;
        // 
        // _btnOutputUnlock
        // 
        _btnOutputUnlock.Location = new Point(93, 3);
        _btnOutputUnlock.Name = "_btnOutputUnlock";
        _btnOutputUnlock.Size = new Size(84, 23);
        _btnOutputUnlock.TabIndex = 0;
        _btnOutputUnlock.Text = "编辑解锁";
        // 
        // _btnOutputImport
        // 
        _btnOutputImport.Location = new Point(3, 3);
        _btnOutputImport.Name = "_btnOutputImport";
        _btnOutputImport.Size = new Size(84, 23);
        _btnOutputImport.TabIndex = 1;
        _btnOutputImport.Text = "导入数据";
        // 
        // _outputToolbar
        // 
        _outputToolbar.Controls.Add(_btnOutputImport);
        _outputToolbar.Controls.Add(_btnOutputUnlock);
        _outputToolbar.Dock = DockStyle.Top;
        _outputToolbar.Location = new Point(8, 8);
        _outputToolbar.Name = "_outputToolbar";
        _outputToolbar.Padding = new Padding(0, 0, 0, 4);
        _outputToolbar.Size = new Size(1376, 40);
        _outputToolbar.TabIndex = 0;
        _outputToolbar.WrapContents = false;
        // 
        // DebugToolsPageWindow
        // 
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(1400, 900);
        Controls.Add(_contentHost);
        Name = "DebugToolsPageWindow";
        Text = "调试按钮";
        ZoomScaleRect = new Rectangle(15, 15, 1400, 900);
        _contentHost.ResumeLayout(false);
        _previewTab.ResumeLayout(false);
        _tabAxisGroup.ResumeLayout(false);
        _axisSplitPreview.Panel1.ResumeLayout(false);
        _axisSplitPreview.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_axisSplitPreview).EndInit();
        _axisSplitPreview.ResumeLayout(false);
        _tabOutput.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_outputGridPreview).EndInit();
        _outputToolbar.ResumeLayout(false);
        ResumeLayout(false);
    }

    private DataGridViewTextBoxColumn dataGridViewTextBoxColumn1;
    private DataGridViewTextBoxColumn dataGridViewTextBoxColumn2;
    private DataGridViewTextBoxColumn dataGridViewTextBoxColumn3;
    private DataGridViewTextBoxColumn dataGridViewTextBoxColumn4;
    private FlowLayoutPanel _outputToolbar;
    private Button _btnOutputImport;
    private Button _btnOutputUnlock;
}
