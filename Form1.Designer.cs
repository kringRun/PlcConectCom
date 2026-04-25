using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

partial class HB_HMI_MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        _mainSplit = new SplitContainer();
        _mainLeftPanel = new TableLayoutPanel();
        _lblMainAlarmTitle = new Label();
        _mainRightPanel = new TableLayoutPanel();
        _lblTopAlarmTitle = new Label();
        _lblTopAlarm1 = new Label();
        _lblTopAlarm2 = new Label();
        _lblTopAlarm3 = new Label();
        _lblRealtimeDataTitle = new Label();
        _debugTab = new UITabControl();
        _tabParameter = new TabPage();
        _tabHmiDisplayData = new TabPage();
        _tabButtonVar = new TabPage();
        _tabAxis = new TabPage();
        _axisSplit = new SplitContainer();
        _tabBoolGroup = new TabPage();
        _tabAlarmConfig = new TabPage();
        _tabTaskSettings = new TabPage();
        _tabCommunicationConfig = new TabPage();
        _recordToolbar = new FlowLayoutPanel();
        _lblRecordFrom = new Label();
        _lblRecordTo = new Label();
        _recordHint = new Label();
        ((System.ComponentModel.ISupportInitialize)_mainSplit).BeginInit();
        _mainSplit.Panel1.SuspendLayout();
        _mainSplit.Panel2.SuspendLayout();
        _mainSplit.SuspendLayout();
        _mainLeftPanel.SuspendLayout();
        _mainRightPanel.SuspendLayout();
        _debugTab.SuspendLayout();
        _tabAxis.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_axisSplit).BeginInit();
        _axisSplit.SuspendLayout();
        _recordToolbar.SuspendLayout();
        SuspendLayout();
        // 
        // _mainSplit
        // 
        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.Location = new Point(0, 0);
        _mainSplit.Name = "_mainSplit";
        // 
        // _mainSplit.Panel1
        // 
        _mainSplit.Panel1.Controls.Add(_mainLeftPanel);
        // 
        // _mainSplit.Panel2
        // 
        _mainSplit.Panel2.Controls.Add(_mainRightPanel);
        _mainSplit.Size = new Size(150, 100);
        _mainSplit.SplitterDistance = 120;
        _mainSplit.TabIndex = 0;
        // 
        // _mainLeftPanel
        // 
        _mainLeftPanel.ColumnCount = 1;
        _mainLeftPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _mainLeftPanel.Controls.Add(_lblMainAlarmTitle, 0, 0);
        _mainLeftPanel.Dock = DockStyle.Fill;
        _mainLeftPanel.Location = new Point(0, 0);
        _mainLeftPanel.Name = "_mainLeftPanel";
        _mainLeftPanel.Padding = new Padding(6);
        _mainLeftPanel.RowCount = 2;
        _mainLeftPanel.RowStyles.Add(new RowStyle());
        _mainLeftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainLeftPanel.Size = new Size(120, 100);
        _mainLeftPanel.TabIndex = 0;
        // 
        // _lblMainAlarmTitle
        // 
        _lblMainAlarmTitle.AutoSize = true;
        _lblMainAlarmTitle.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _lblMainAlarmTitle.Location = new Point(9, 6);
        _lblMainAlarmTitle.Name = "_lblMainAlarmTitle";
        _lblMainAlarmTitle.Padding = new Padding(0, 0, 0, 4);
        _lblMainAlarmTitle.Size = new Size(90, 48);
        _lblMainAlarmTitle.TabIndex = 0;
        _lblMainAlarmTitle.Text = "实时报警列表";
        // 
        // _mainRightPanel
        // 
        _mainRightPanel.ColumnCount = 1;
        _mainRightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _mainRightPanel.Controls.Add(_lblTopAlarmTitle, 0, 0);
        _mainRightPanel.Controls.Add(_lblTopAlarm1, 0, 1);
        _mainRightPanel.Controls.Add(_lblTopAlarm2, 0, 2);
        _mainRightPanel.Controls.Add(_lblTopAlarm3, 0, 3);
        _mainRightPanel.Controls.Add(_lblRealtimeDataTitle, 0, 4);
        _mainRightPanel.Dock = DockStyle.Fill;
        _mainRightPanel.Location = new Point(0, 0);
        _mainRightPanel.Name = "_mainRightPanel";
        _mainRightPanel.Padding = new Padding(6);
        _mainRightPanel.RowCount = 6;
        _mainRightPanel.RowStyles.Add(new RowStyle());
        _mainRightPanel.RowStyles.Add(new RowStyle());
        _mainRightPanel.RowStyles.Add(new RowStyle());
        _mainRightPanel.RowStyles.Add(new RowStyle());
        _mainRightPanel.RowStyles.Add(new RowStyle());
        _mainRightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainRightPanel.Size = new Size(26, 100);
        _mainRightPanel.TabIndex = 0;
        // 
        // _lblTopAlarmTitle
        // 
        _lblTopAlarmTitle.AutoSize = true;
        _lblTopAlarmTitle.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _lblTopAlarmTitle.Location = new Point(9, 6);
        _lblTopAlarmTitle.Name = "_lblTopAlarmTitle";
        _lblTopAlarmTitle.Padding = new Padding(0, 0, 0, 8);
        _lblTopAlarmTitle.Size = new Size(8, 184);
        _lblTopAlarmTitle.TabIndex = 0;
        _lblTopAlarmTitle.Text = "报警统计 Top3";
        // 
        // _lblTopAlarm1
        // 
        _lblTopAlarm1.BackColor = Color.FromArgb(255, 235, 238);
        _lblTopAlarm1.Dock = DockStyle.Top;
        _lblTopAlarm1.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _lblTopAlarm1.ForeColor = Color.Firebrick;
        _lblTopAlarm1.Location = new Point(9, 190);
        _lblTopAlarm1.Name = "_lblTopAlarm1";
        _lblTopAlarm1.Padding = new Padding(8, 0, 0, 0);
        _lblTopAlarm1.Size = new Size(8, 42);
        _lblTopAlarm1.TabIndex = 1;
        _lblTopAlarm1.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _lblTopAlarm2
        // 
        _lblTopAlarm2.BackColor = Color.FromArgb(255, 243, 224);
        _lblTopAlarm2.Dock = DockStyle.Top;
        _lblTopAlarm2.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _lblTopAlarm2.ForeColor = Color.DarkOrange;
        _lblTopAlarm2.Location = new Point(9, 232);
        _lblTopAlarm2.Name = "_lblTopAlarm2";
        _lblTopAlarm2.Padding = new Padding(8, 0, 0, 0);
        _lblTopAlarm2.Size = new Size(8, 42);
        _lblTopAlarm2.TabIndex = 2;
        _lblTopAlarm2.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _lblTopAlarm3
        // 
        _lblTopAlarm3.BackColor = Color.FromArgb(255, 253, 231);
        _lblTopAlarm3.Dock = DockStyle.Top;
        _lblTopAlarm3.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _lblTopAlarm3.ForeColor = Color.DarkGoldenrod;
        _lblTopAlarm3.Location = new Point(9, 274);
        _lblTopAlarm3.Name = "_lblTopAlarm3";
        _lblTopAlarm3.Padding = new Padding(8, 0, 0, 0);
        _lblTopAlarm3.Size = new Size(8, 42);
        _lblTopAlarm3.TabIndex = 3;
        _lblTopAlarm3.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _lblRealtimeDataTitle
        // 
        _lblRealtimeDataTitle.AutoSize = true;
        _lblRealtimeDataTitle.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _lblRealtimeDataTitle.Location = new Point(9, 316);
        _lblRealtimeDataTitle.Name = "_lblRealtimeDataTitle";
        _lblRealtimeDataTitle.Padding = new Padding(0, 8, 0, 4);
        _lblRealtimeDataTitle.Size = new Size(8, 144);
        _lblRealtimeDataTitle.TabIndex = 4;
        _lblRealtimeDataTitle.Text = "实时工艺数据";
        // 
        // _debugTab
        // 
        _debugTab.Controls.Add(_tabParameter);
        _debugTab.Controls.Add(_tabHmiDisplayData);
        _debugTab.Controls.Add(_tabButtonVar);
        _debugTab.Controls.Add(_tabAxis);
        _debugTab.Controls.Add(_tabBoolGroup);
        _debugTab.Controls.Add(_tabAlarmConfig);
        _debugTab.Controls.Add(_tabTaskSettings);
        _debugTab.Controls.Add(_tabCommunicationConfig);
        _debugTab.Dock = DockStyle.Fill;
        _debugTab.DrawMode = TabDrawMode.OwnerDrawFixed;
        _debugTab.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
        _debugTab.ItemSize = new Size(150, 40);
        _debugTab.Location = new Point(0, 0);
        _debugTab.MainPage = "";
        _debugTab.Name = "_debugTab";
        _debugTab.SelectedIndex = 0;
        _debugTab.Size = new Size(200, 100);
        _debugTab.SizeMode = TabSizeMode.Fixed;
        _debugTab.TabIndex = 0;
        _debugTab.TabUnSelectedForeColor = Color.FromArgb(240, 240, 240);
        _debugTab.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
        // 
        // _tabParameter
        // 
        _tabParameter.Location = new Point(0, 40);
        _tabParameter.Name = "_tabParameter";
        _tabParameter.Size = new Size(200, 60);
        _tabParameter.TabIndex = 0;
        _tabParameter.Text = "参数设置";
        // 
        // _tabHmiDisplayData
        // 
        _tabHmiDisplayData.Location = new Point(0, 40);
        _tabHmiDisplayData.Name = "_tabHmiDisplayData";
        _tabHmiDisplayData.Size = new Size(200, 60);
        _tabHmiDisplayData.TabIndex = 1;
        _tabHmiDisplayData.Text = "HMI显示数据";
        // 
        // _tabButtonVar
        // 
        _tabButtonVar.Location = new Point(0, 40);
        _tabButtonVar.Name = "_tabButtonVar";
        _tabButtonVar.Size = new Size(200, 60);
        _tabButtonVar.TabIndex = 2;
        _tabButtonVar.Text = "按钮类变量";
        // 
        // _tabAxis
        // 
        _tabAxis.Controls.Add(_axisSplit);
        _tabAxis.Location = new Point(0, 40);
        _tabAxis.Name = "_tabAxis";
        _tabAxis.Size = new Size(200, 60);
        _tabAxis.TabIndex = 3;
        _tabAxis.Text = "轴组控制";
        // 
        // _axisSplit
        // 
        _axisSplit.Dock = DockStyle.Fill;
        _axisSplit.Location = new Point(0, 0);
        _axisSplit.Name = "_axisSplit";
        _axisSplit.Orientation = Orientation.Horizontal;
        _axisSplit.Size = new Size(200, 60);
        _axisSplit.SplitterDistance = 31;
        _axisSplit.TabIndex = 0;
        // 
        // _tabBoolGroup
        // 
        _tabBoolGroup.Location = new Point(0, 40);
        _tabBoolGroup.Name = "_tabBoolGroup";
        _tabBoolGroup.Size = new Size(200, 60);
        _tabBoolGroup.TabIndex = 4;
        _tabBoolGroup.Text = "输出点位";
        // 
        // _tabAlarmConfig
        // 
        _tabAlarmConfig.Location = new Point(0, 40);
        _tabAlarmConfig.Name = "_tabAlarmConfig";
        _tabAlarmConfig.Size = new Size(200, 60);
        _tabAlarmConfig.TabIndex = 5;
        _tabAlarmConfig.Text = "报警配置";
        // 
        // _tabTaskSettings
        // 
        _tabTaskSettings.Location = new Point(0, 40);
        _tabTaskSettings.Name = "_tabTaskSettings";
        _tabTaskSettings.Size = new Size(200, 60);
        _tabTaskSettings.TabIndex = 6;
        _tabTaskSettings.Text = "任务设置";
        // 
        // _tabCommunicationConfig
        // 
        _tabCommunicationConfig.Location = new Point(0, 40);
        _tabCommunicationConfig.Name = "_tabCommunicationConfig";
        _tabCommunicationConfig.Size = new Size(200, 60);
        _tabCommunicationConfig.TabIndex = 7;
        _tabCommunicationConfig.Text = "通讯配置";
        // 
        // _recordToolbar
        // 
        _recordToolbar.Controls.Add(_lblRecordFrom);
        _recordToolbar.Controls.Add(_lblRecordTo);
        _recordToolbar.Dock = DockStyle.Top;
        _recordToolbar.Location = new Point(0, 0);
        _recordToolbar.Name = "_recordToolbar";
        _recordToolbar.Padding = new Padding(6);
        _recordToolbar.Size = new Size(200, 42);
        _recordToolbar.TabIndex = 0;
        _recordToolbar.WrapContents = false;
        // 
        // _lblRecordFrom
        // 
        _lblRecordFrom.AutoSize = true;
        _lblRecordFrom.Location = new Point(9, 6);
        _lblRecordFrom.Name = "_lblRecordFrom";
        _lblRecordFrom.Padding = new Padding(0, 8, 0, 0);
        _lblRecordFrom.Size = new Size(56, 25);
        _lblRecordFrom.TabIndex = 0;
        _lblRecordFrom.Text = "开始日期";
        // 
        // _lblRecordTo
        // 
        _lblRecordTo.AutoSize = true;
        _lblRecordTo.Location = new Point(71, 6);
        _lblRecordTo.Name = "_lblRecordTo";
        _lblRecordTo.Padding = new Padding(8, 8, 0, 0);
        _lblRecordTo.Size = new Size(64, 25);
        _lblRecordTo.TabIndex = 1;
        _lblRecordTo.Text = "结束日期";
        // 
        // _recordHint
        // 
        _recordHint.Dock = DockStyle.Top;
        _recordHint.Font = new Font("Microsoft YaHei UI", 12F);
        _recordHint.Location = new Point(0, 0);
        _recordHint.Name = "_recordHint";
        _recordHint.Size = new Size(100, 26);
        _recordHint.TabIndex = 0;
        _recordHint.Text = "报警记录历史查询";
        _recordHint.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // HB_HMI_MainForm
        // 
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(1920, 1080);
        Name = "HB_HMI_MainForm";
        Text = "PLC 运行平台";
        ZoomScaleRect = new Rectangle(15, 15, 1920, 1080);
        _mainSplit.Panel1.ResumeLayout(false);
        _mainSplit.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_mainSplit).EndInit();
        _mainSplit.ResumeLayout(false);
        _mainLeftPanel.ResumeLayout(false);
        _mainLeftPanel.PerformLayout();
        _mainRightPanel.ResumeLayout(false);
        _mainRightPanel.PerformLayout();
        _debugTab.ResumeLayout(false);
        _tabAxis.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_axisSplit).EndInit();
        _axisSplit.ResumeLayout(false);
        _recordToolbar.ResumeLayout(false);
        _recordToolbar.PerformLayout();
        ResumeLayout(false);
    }

    #endregion
    // 主页面布局控件
    private SplitContainer _mainSplit;
    private TableLayoutPanel _mainLeftPanel;
    private Label _lblMainAlarmTitle;
    private TableLayoutPanel _mainRightPanel;
    private Label _lblTopAlarmTitle;
    private Label _lblTopAlarm1;
    private Label _lblTopAlarm2;
    private Label _lblTopAlarm3;
    private Label _lblRealtimeDataTitle;
    // 调试页面控件
    private UITabControl _debugTab;
    private TabPage _tabParameter;
    private TabPage _tabHmiDisplayData;
    private TabPage _tabButtonVar;
    private TabPage _tabAxis;
    private SplitContainer _axisSplit;
    private TabPage _tabBoolGroup;
    private TabPage _tabAlarmConfig;
    private TabPage _tabTaskSettings;
    private TabPage _tabCommunicationConfig;
    // 记录查询页面控件
    private FlowLayoutPanel _recordToolbar;
    private Label _lblRecordFrom;
    private Label _lblRecordTo;
    private Label _recordHint;
}
