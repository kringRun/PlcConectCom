using System.Runtime.InteropServices;



namespace PlcConectCom.WinFormsTest;



/// <summary>

/// 在独立「页窗体」（<see cref="IPageHostWindow"/>）上打开对话框时，

/// WinForms 的 <see cref="Form.ShowDialog()"/> 会阻塞同一线程上其它所有窗体。

/// 此处用 <see cref="Form.Show"/> + 消息泵，仅通过 Win32 禁用页窗体，使主界面等仍可操作。

/// </summary>

internal static class SemiModalDialogHost

{

    [DllImport("user32.dll")]

    private static extern bool EnableWindow(IntPtr hWnd, bool enable);



    public static DialogResult ShowDialog(Form dialog, Form ownerPageHost)

    {

        if (dialog.IsDisposed)

            return DialogResult.Cancel;



        dialog.Owner = ownerPageHost;

        DialogResult completed = DialogResult.Cancel;



        void Complete(DialogResult result)

        {

            completed = result;

            if (dialog.IsDisposed)

                return;



            if (dialog.DialogResult == DialogResult.None)

                dialog.DialogResult = result;



            if (dialog.Visible)

                dialog.Close();

        }



        WireDialogResultClose(dialog, Complete);

        // 关键：本宿主用 Show() 显示弹框，而 Show() 显示的窗体在 Close() 时会连带 Dispose，
        // 会带来两个后果：
        //   ① 「确定/保存」按钮若没有 DialogResult（在 Click 里手工 dlg.DialogResult = OK 再 Close），
        //      WireDialogResultClose 挂不到它，循环外补读又因 IsDisposed 被跳过 → 误判成 Cancel，
        //      保存后的写入被整体跳过（轴组控制「编辑数据」就是这个症状）；
        //   ② 调用方在 ShowDialog 返回后读控件内容（TextBox.Text 等）会拿到**空串**，
        //      实测输入 123.5 读回空 → 每个字段都被当成空值静默跳过。
        // 因此在 FormClosing（Dispose 之前）记录结果，并拦截关闭改为 Hide()，
        // 让控件内容在返回后仍可读；真正的 Dispose 交给调用方的 using。
        dialog.FormClosing += (_, e) =>
        {
            if (dialog.DialogResult != DialogResult.None)
                completed = dialog.DialogResult;

            // 仅拦截用户/代码主动关闭；应用退出、宿主关闭等场景放行，避免挡住正常关闭流程。
            if (e.CloseReason is CloseReason.None or CloseReason.UserClosing)
            {
                e.Cancel = true;
                dialog.Hide();
            }
        };



        EnableWindow(ownerPageHost.Handle, false);

        try

        {

            dialog.Show();

            while (dialog.Visible)

            {

                if (ownerPageHost.IsDisposed)

                {

                    try

                    {

                        dialog.Close();

                    }

                    catch

                    {

                        // ignore

                    }



                    break;

                }



                if (dialog.DialogResult != DialogResult.None)

                {

                    Complete(dialog.DialogResult);

                    break;

                }



                Application.DoEvents();

            }



            // 在 DoEvents 里同步 Close() 时，下一轮 while 因 Visible=false 直接退出，

            // 必须在循环外再读一次 DialogResult，否则会误返回 Cancel。

            // DialogResult 在窗体 Dispose 之后仍可读，故不再以 IsDisposed 为条件，
            // 避免在 Show()+Close()（会 Dispose）的场景下漏读结果。
            if (dialog.DialogResult != DialogResult.None)

                completed = dialog.DialogResult;



            return completed;

        }

        finally

        {

            if (!ownerPageHost.IsDisposed)

                EnableWindow(ownerPageHost.Handle, true);

        }

    }



    private static void WireDialogResultClose(Form dialog, Action<DialogResult> complete)

    {

        void Visit(Control parent)

        {

            foreach (Control child in parent.Controls)

            {

                if (child is Button { DialogResult: var result and not DialogResult.None })

                    child.Click += (_, _) => complete(result);



                if (child.HasChildren)

                    Visit(child);

            }

        }



        Visit(dialog);

    }

}


