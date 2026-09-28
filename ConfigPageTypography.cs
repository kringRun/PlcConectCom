namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 系统配置页排版规范：标题（页签 / 分组标题 / 表格列头）与正文（表格数据 / 按钮 / 输入框）
/// 用「字号 + 粗细 + 颜色深浅」三级区分，全页统一微软雅黑，替换默认的宋体小字。
/// <para>
/// 采用后序遍历（先子控件、后父容器）设置字体：父容器的字体是环境属性，
/// 先给子控件赋过字体后，父容器再改就不会把子控件的字体覆盖回去（避免标题粗体被带到正文）。
/// </para>
/// </summary>
internal static class ConfigPageTypography
{
    /// <summary>子控件/子页若不希望被统一排版，可设 Tag 为该值，自身与子树整体跳过。</summary>
    public const string KeepTag = "TypographyKeep";

    private const string FontName = "Microsoft YaHei UI";

    /// <summary>一级标题：页签。</summary>
    public static readonly Font TitleFont = new(FontName, 10F, FontStyle.Bold);

    /// <summary>二级标题：分组标题 / 表格列头。</summary>
    public static readonly Font GroupTitleFont = new(FontName, 9.5F, FontStyle.Bold);

    /// <summary>正文：表格数据 / 按钮 / 输入框。</summary>
    public static readonly Font BodyFont = new(FontName, 9F, FontStyle.Regular);

    /// <summary>正文强调：字段标签等比数据行稍重一档。</summary>
    public static readonly Font BodyEmphasisFont = new(FontName, 9.5F, FontStyle.Regular);

    // 标题深色 vs 正文浅一档：同色系不同明度，形成层级
    private static readonly Color TitleForeColor = Color.FromArgb(30, 46, 74);
    private static readonly Color BodyForeColor = Color.FromArgb(95, 105, 120);

    private static readonly Color HeaderBackColor = Color.FromArgb(233, 239, 248);
    private static readonly Color HeaderForeColor = Color.FromArgb(30, 46, 74);
    private static readonly Color GridLineColor = Color.FromArgb(223, 230, 240);
    private static readonly Color AltRowBackColor = Color.FromArgb(250, 251, 253);
    private static readonly Color SelectionBackColor = Color.FromArgb(214, 231, 249);

    private const int GridColumnHeaderHeight = 34;
    private const int GridRowHeight = 26;

    /// <summary>对整棵控件树套用排版规范（可重复调用）。</summary>
    public static void Apply(Control? root)
    {
        if (root is null)
            return;

        ApplyCore(root);
    }

    private static void ApplyCore(Control control)
    {
        if (control.Tag is string tag && tag == KeepTag)
            return;

        foreach (Control child in control.Controls)
            ApplyCore(child);

        StyleControl(control);
    }

    private static void StyleControl(Control control)
    {
        // Sunny 控件按自身 Style 上色，只统一字体，避免破坏其配色
        if (string.Equals(control.GetType().Namespace, "Sunny.UI", StringComparison.Ordinal))
        {
            switch (control)
            {
                case TabControl:
                    control.Font = TitleFont;
                    break;
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                default:
                    control.Font = BodyFont;
                    break;
            }

            return;
        }

        switch (control)
        {
            case TabControl: // 页签文字＝标题级
                control.Font = TitleFont;
                control.ForeColor = TitleForeColor;
                break;
            case DataGridView grid:
                grid.Font = BodyFont;
                grid.ForeColor = BodyForeColor;
                StyleGrid(grid);
                break;
            case GroupBox: // 分组标题＝标题级
                control.Font = GroupTitleFont;
                control.ForeColor = TitleForeColor;
                break;
            case Label: // 字段标签＝正文强调
                control.Font = BodyEmphasisFont;
                control.ForeColor = BodyForeColor;
                break;
            case Button:
            case CheckBox:
            case RadioButton:
                control.Font = BodyFont;
                control.ForeColor = BodyForeColor;
                break;
            case TextBoxBase:
            case ComboBox:
            case NumericUpDown:
            case DateTimePicker:
                control.Font = BodyFont;
                control.ForeColor = BodyForeColor;
                break;
            default: // 容器等：统一字体并阻断父级粗体向下传递
                control.Font = BodyFont;
                break;
        }
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Color.White;
        grid.GridColor = GridLineColor;

        // 列头＝标题级：大一号、加粗、深色 + 浅蓝底
        grid.ColumnHeadersDefaultCellStyle.Font = GroupTitleFont;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = HeaderForeColor;
        grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = HeaderForeColor;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        grid.ColumnHeadersHeight = GridColumnHeaderHeight;

        // 数据＝正文级：小一号、常规、浅一档灰
        grid.DefaultCellStyle.Font = BodyFont;
        grid.DefaultCellStyle.ForeColor = BodyForeColor;
        grid.DefaultCellStyle.SelectionBackColor = SelectionBackColor;
        grid.DefaultCellStyle.SelectionForeColor = TitleForeColor;
        grid.AlternatingRowsDefaultCellStyle.BackColor = AltRowBackColor;

        grid.RowHeadersDefaultCellStyle.Font = BodyFont;
        grid.RowHeadersDefaultCellStyle.ForeColor = BodyForeColor;
        grid.RowHeadersDefaultCellStyle.SelectionBackColor = SelectionBackColor;
        grid.RowHeadersDefaultCellStyle.SelectionForeColor = TitleForeColor;

        grid.RowTemplate.Height = GridRowHeight;
        foreach (DataGridViewRow row in grid.Rows)
            row.Height = GridRowHeight;
    }
}
