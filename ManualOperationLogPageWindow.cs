using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

/// <summary>「手动操作日志」独立窗口，宿主为 <see cref="ManualOperationLogPageControl"/>。</summary>
public sealed class ManualOperationLogPageWindow : UIForm, IPageHostWindow
{
    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill, BackColor = Color.White };

    public ManualOperationLogPageWindow()
    {
        Text = "手动操作日志";
        Font = new Font("Microsoft YaHei UI", 10F);
        ClientSize = new Size(1400, 880);
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        MinimizeBox = true;
        Controls.Add(_contentHost);
    }

    public void AttachPage(Control page)
    {
        if (ReferenceEquals(page.Parent, _contentHost) && page.Dock == DockStyle.Fill)
        {
            page.Visible = true;
            page.BringToFront();
            return;
        }

        page.Parent?.Controls.Remove(page);
        page.Dock = DockStyle.Fill;
        _contentHost.Controls.Clear();
        _contentHost.Controls.Add(page);
        page.Visible = true;
        page.BringToFront();
    }
}
