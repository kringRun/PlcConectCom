using PlcConectCom.WinForms.Controls;

namespace PlcConectCom.WinFormsTest;

public partial class HB_HMI_MainForm
{
    private bool _updatingResponsiveLayout;

    private void InitializeResponsiveLayout()
    {
        // 页面会在主窗与独立窗口间移动，按实际宿主宽度调整，而非仅按显示器判断。
        _mainPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        _debugPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        ClientSizeChanged += (_, _) => UpdateResponsiveLayout();
        _navLeftPanel.SizeChanged += (_, _) => UpdateResponsiveLayout();
        FitDisplayControls(_mainButtonDisplayPanel);
        FitDisplayControls(_mainProcessRealtimeFlow);
        FitDisplayControls(_mainPlcBoolLabelPanel);
        FitDisplayControls(_mainPlcValueTextBoxPanel);
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (_updatingResponsiveLayout || IsDisposed)
            return;
        _updatingResponsiveLayout = true;
        try
        {
            bool compact = ClientSize.Width < 1280 || ClientSize.Height < 800;
            _navPanel.Width = compact ? 156 : 188;
            _navPanel.Padding = compact ? new Padding(6) : new Padding(10, 12, 10, 12);
            _navRightPanel.Height = compact ? 112 : 120;
            int navWidth = Math.Max(80, _navLeftPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (Control button in _navLeftPanel.Controls)
            {
                button.Width = Math.Min(164, navWidth);
                button.Height = compact ? 32 : button == _btnParameterChangeRecords ? 44 : 48;
                button.Margin = new Padding(0, 0, 0, compact ? 4 : 8);
            }
            _cmbUserRole.Width = Math.Min(164, _navRightPanel.ClientSize.Width);
            _btnPasswordManage.Width = _cmbUserRole.Width;
            _txtLog.Height = compact ? 96 : 180;

            bool compactMain = _mainPage.ClientSize.Width < 1280 || _mainPage.ClientSize.Height < 800;
            _mainPage.Padding = new Padding(compactMain ? 4 : 8);
            _mainLeftPanel.Padding = _mainRightPanel.Padding = new Padding(compactMain ? 6 : 10);
            if (_mainLeftPanel.RowStyles.Count > 1)
                _mainLeftPanel.RowStyles[1].Height = compactMain
                    ? Math.Clamp(_mainPage.ClientSize.Height * 0.36f, 140, 260)
                    : MainPageAlarmGridSectionHeight;

            int splitWidth = _mainSplit.ClientSize.Width - _mainSplit.SplitterWidth;
            if (splitWidth > 200)
            {
                int target = (int)(splitWidth * (compactMain ? 0.80 : 0.84));
                int maximum = splitWidth - _mainSplit.Panel2MinSize;
                if (maximum >= _mainSplit.Panel1MinSize)
                    _mainSplit.SplitterDistance = Math.Clamp(target, _mainSplit.Panel1MinSize, maximum);
            }
            foreach (Label label in _topAlarmLabels)
                label.Height = compactMain ? 28 : 40;

            // 多页签在窄窗口分行，主/子 PLC 使用相同的配置页布局。
            bool compactConfig = _debugPage.ClientSize.Width < 1280;
            _debugTab.Multiline = true;
            _debugTab.ItemSize = compactConfig ? new Size(108, 28) : new Size(150, 40);
        }
        finally
        {
            _updatingResponsiveLayout = false;
        }
    }

    private static void FitDisplayControls(FlowLayoutPanel panel)
    {
        var originalSizes = new Dictionary<Control, (Size Size, Size Minimum, Size Maximum)>();
        bool arranging = false;
        void Arrange()
        {
            if (arranging || panel.IsDisposed || panel.ClientSize.Width < 80)
                return;
            arranging = true;
            try
            {
                // 预留滚动条，避免纵向溢出后控件又被横向截断。
                int available = panel.ClientSize.Width - panel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
                bool compact = (panel.FindForm()?.ClientSize.Width ?? 1920) < 1280;
                foreach (Control control in panel.Controls)
                {
                    if (!originalSizes.TryGetValue(control, out var original))
                    {
                        original = (control.Size, control.MinimumSize, control.MaximumSize);
                        originalSizes.Add(control, original);
                    }
                    int width = Math.Max(1, Math.Min(original.Size.Width, available - control.Margin.Horizontal));
                    int height = compact && control is PlcBoundButton ? 36 : original.Size.Height;
                    if (width < original.Size.Width || height < original.Size.Height)
                    {
                        // PLC 按钮原先固定 280×44，容器收窄时需同时放开尺寸约束。
                        control.MinimumSize = new Size(0, Math.Min(original.Minimum.Height, height));
                        control.MaximumSize = original.Maximum.Width > 0 ? new Size(width, height) : original.Maximum;
                        control.Size = new Size(width, height);
                    }
                    else
                    {
                        control.MaximumSize = original.Maximum;
                        control.MinimumSize = original.Minimum;
                        control.Size = original.Size;
                    }
                }
            }
            finally
            {
                arranging = false;
            }
        }
        panel.Layout += (_, _) => Arrange();
        panel.ControlRemoved += (_, e) => { if (e.Control != null) originalSizes.Remove(e.Control); };
        Arrange();
    }
}
