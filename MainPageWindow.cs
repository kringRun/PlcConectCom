using Sunny.UI;

namespace PlcConectCom.WinFormsTest;

public partial class MainPageWindow : UIForm, IPageHostWindow
{
    public MainPageWindow()
    {
        InitializeComponent();
    }

    public void AttachPage(Control page)
    {
        // 已在内容区时勿反复 Remove/Add，否则会触发布局/自动缩放叠加导致控件越变越大
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
