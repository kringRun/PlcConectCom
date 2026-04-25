using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

public partial class SystemConfigPageWindow : UIForm, IPageHostWindow
{
    public SystemConfigPageWindow()
    {
        InitializeComponent();
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
