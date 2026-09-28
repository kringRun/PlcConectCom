using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

public sealed class ParameterAdjustPageWindow : UIForm, IPageHostWindow
{
    private readonly Panel _contentHost = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(8)
    };

    public ParameterAdjustPageWindow()
    {
        Text = "参数调整";
        ClientSize = new Size(1400, 900);
        MinimizeBox = true;
        MaximizeBox = true;
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
