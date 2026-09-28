using System.Runtime.CompilerServices;

namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 统一表格行为：禁止列头排序、禁止用鼠标拖拽调节行高；含后续新增的列。适用于 DataGridView / SunnyUI.UIDataGridView。
/// </summary>
internal static class DataGridViewNoSortHelper
{
    private static readonly ConditionalWeakTable<DataGridView, object?> SortDisabledGrids = new();
    private static readonly ConditionalWeakTable<Control, object?> ControlAddedWired = new();

    /// <summary>
    /// 对已有控件树中的全部 DataGridView 应用上述行为，并为后续动态添加的控件继续套用。
    /// </summary>
    public static void WireControlSubtreeForNoColumnSort(Control root)
    {
        if (root is null || root.IsDisposed)
            return;

        void Visit(Control c)
        {
            if (c.IsDisposed)
                return;

            if (c is DataGridView dgv)
                ApplyStandardDataGridViewBehavior(dgv);

            if (!ControlAddedWired.TryGetValue(c, out _))
            {
                ControlAddedWired.Add(c, null);
                c.ControlAdded += (_, e) =>
                {
                    if (e.Control is not null)
                        Visit(e.Control);
                };
            }

            foreach (Control child in c.Controls)
                Visit(child);
        }

        Visit(root);
    }

    private static void ApplyStandardDataGridViewBehavior(DataGridView dgv)
    {
        if (SortDisabledGrids.TryGetValue(dgv, out _))
            return;

        SortDisabledGrids.Add(dgv, null);

        dgv.AllowUserToResizeRows = false;

        foreach (DataGridViewColumn col in dgv.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        dgv.ColumnAdded += (_, e) =>
            e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
    }
}
