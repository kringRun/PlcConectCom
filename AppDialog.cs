namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 从主窗体嵌入页打开的对话框仍使用标准 <see cref="Form.ShowDialog"/>；
/// 从独立页窗体（系统配置、调试工具、监控等）打开时，使用半模态，避免卡住整个程序其它窗体。
/// </summary>
internal static class AppDialog
{
    public static DialogResult ShowDialog(Form dialog) => dialog.ShowDialog();

    public static DialogResult ShowDialog(Form dialog, Control? ownerControl)
    {
        if (ownerControl is null)
            return dialog.ShowDialog();

        Form? ownerForm = ownerControl.FindForm();
        return ShowDialog(dialog, ownerForm);
    }

    public static DialogResult ShowDialog(Form dialog, IWin32Window? owner)
    {
        if (owner is Form ownerForm && ownerForm is IPageHostWindow)
            return SemiModalDialogHost.ShowDialog(dialog, ownerForm);

        if (owner is null)
            return dialog.ShowDialog();

        return dialog.ShowDialog(owner);
    }

    public static DialogResult ShowDialog(Form dialog, Form? ownerForm)
    {
        if (ownerForm is IPageHostWindow)
            return SemiModalDialogHost.ShowDialog(dialog, ownerForm);

        if (ownerForm is null)
            return dialog.ShowDialog();

        return dialog.ShowDialog(ownerForm);
    }
}
