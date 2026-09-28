namespace PlcConectCom.WinFormsTest;

/// <summary>
/// 主窗体底部状态栏统一排版：字体、字号、行高、内边距与分隔线全部收口到这里，
/// 避免各项由默认字体 + 各自 Padding 拼接导致的字号不一、行高跳动、文字贴边。
/// <para>
/// 约定：
/// <list type="bullet">
/// <item>全部项目统一微软雅黑 10F（比正文 9F 大一档，1080p 下易读）；</item>
/// <item>状态文本一律通过 <see cref="SetText"/> 更新，链接项同时改 LinkColor——
/// <see cref="ToolStripStatusLabel"/> 处于 <c>IsLink</c> 时 <c>ForeColor</c> 不生效，
/// 只设 ForeColor 会出现「断开仍显示蓝色」的假状态；</item>
/// <item>闪烁只改颜色、不改文本，避免每秒因文字宽度变化让后面几项左右跳动。</item>
/// </list>
/// </para>
/// </summary>
internal static class StatusStripStyle
{
    /// <summary>单个状态的左右内边距（px）。</summary>
    private const int ItemPaddingX = 12;
    /// <summary>
    /// 单个状态的上下内边距（px）。状态栏高度由它撑开（文字行高 + 2×该值 ≈ 35px），
    /// 不直接把 StatusStrip.Height 写死——否则项目按顶部对齐，文字会贴着上边缘。
    /// </summary>
    private const int ItemPaddingY = 4;
    private const int BarPaddingX = 10;

    public static readonly Font TextFont = new("Microsoft YaHei UI", 10F, FontStyle.Regular);

    /// <summary>常态文本：比纯黑柔和，长时间盯屏不刺眼。</summary>
    public static readonly Color TextColor = Color.FromArgb(64, 74, 88);
    /// <summary>未启用 / 未配置等次要信息。</summary>
    public static readonly Color MutedColor = Color.FromArgb(124, 134, 148);
    /// <summary>正常态（连接成功、心跳正常、自动运行关闭）。</summary>
    public static readonly Color OkColor = Color.FromArgb(21, 132, 76);
    /// <summary>正常态闪烁的暗相：与 <see cref="OkColor"/> 明暗交替，形成呼吸效果。</summary>
    public static readonly Color OkPulseColor = Color.FromArgb(0, 92, 52);
    /// <summary>告警态（连接中、重连失败、自动运行中断读失败）。</summary>
    public static readonly Color WarnColor = Color.FromArgb(183, 108, 0);
    /// <summary>故障态（连接中断、心跳异常）。</summary>
    public static readonly Color ErrorColor = Color.FromArgb(197, 40, 40);
    /// <summary>可点击链接。</summary>
    public static readonly Color LinkColor = Color.FromArgb(0, 96, 170);

    private static readonly Color BarBackColor = Color.FromArgb(246, 248, 251);
    private static readonly Color BarBorderColor = Color.FromArgb(212, 220, 231);
    private static readonly Color SeparatorColor = Color.FromArgb(198, 208, 220);

    /// <summary>单例渲染器：<c>Renderer</c> 默认永不为 null，必须显式赋值才能覆盖渐变底。</summary>
    private static readonly ToolStripRenderer RendererInstance = new StatusStripRenderer();

    /// <summary>对状态栏与其中的标签套用统一排版（可重复调用；须在项目加入后再调用）。</summary>
    public static void Apply(StatusStrip strip)
    {
        strip.AutoSize = true;
        strip.SizingGrip = false;
        strip.ShowItemToolTips = true;
        strip.BackColor = BarBackColor;
        strip.Padding = new Padding(BarPaddingX, 0, BarPaddingX, 0);
        strip.Font = TextFont;
        strip.Renderer = RendererInstance;

        foreach (ToolStripItem item in strip.Items)
        {
            switch (item)
            {
                case ToolStripSeparator separator:
                    separator.Margin = new Padding(0, ItemPaddingY + 2, 0, ItemPaddingY + 2);
                    break;

                case ToolStripStatusLabel label:
                    label.Font = TextFont;
                    label.AutoSize = true;
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    label.Margin = Padding.Empty;
                    // 上下 padding 撑高整条状态栏，同时让文字在条内垂直居中
                    label.Padding = new Padding(ItemPaddingX, ItemPaddingY, ItemPaddingX, ItemPaddingY);
                    if (label.IsLink)
                    {
                        // 常态不画下划线，悬停才显示，减少与状态文字混在一起的「一片下划线」
                        label.LinkBehavior = LinkBehavior.HoverUnderline;
                        label.LinkColor = LinkColor;
                        label.ActiveLinkColor = Color.FromArgb(0, 64, 128);
                        label.VisitedLinkColor = LinkColor;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// 同时更新文本与颜色。链接项 <c>ForeColor</c> 无效，必须改写 <c>LinkColor</c> 族属性。
    /// </summary>
    public static void SetText(ToolStripStatusLabel label, string text, Color color)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;

        if (label.IsLink)
        {
            label.LinkColor = color;
            label.ActiveLinkColor = color;
            label.VisitedLinkColor = color;
        }

        if (label.ForeColor != color)
            label.ForeColor = color;
    }

    /// <summary>扁平渲染：去掉默认渐变底与 3D 边框，改为纯色底 + 上边一条细分隔线。</summary>
    private sealed class StatusStripRenderer : ToolStripProfessionalRenderer
    {
        public StatusStripRenderer()
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(BarBackColor);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(BarBorderColor);
            e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width - 1, 0);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // 注意：这里的 Graphics 原点已被平移到分隔项自身，坐标系是「项目内本地坐标」，
            // 且绘制被裁剪到项目边界内，所以只能按 bounds.Width/Height 画，不能加 bounds.X/Y。
            // 分隔项上下各留 6px 边距，天然形成「只占中间一段」的短线。
            Rectangle bounds = e.Item.Bounds;
            if (bounds.Height <= 0 || bounds.Width <= 0)
                return;

            using var brush = new SolidBrush(SeparatorColor);
            int x = Math.Max(1, bounds.Width / 2);
            e.Graphics.FillRectangle(brush, x, 0, 1, bounds.Height);
        }
    }
}
