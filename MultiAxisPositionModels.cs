namespace PlcConectCom.WinFormsTest;

// 单轴定义
public sealed class AxisDefinition
{
    public required string AxisName { get; init; }

    public string DisplayName { get; init; } = string.Empty;
}

// 点位项：点位名称及各轴目标位置
public sealed class AxisPointItem
{
    public required string PointName { get; init; }

    public Dictionary<string, double> AxisPositions { get; init; } = [];
}

// 页面定义：页面名称、轴清单与点位清单
public sealed class AxisPageDefinition
{
    public required string PageName { get; init; }

    public List<AxisDefinition> Axes { get; init; } = [];

    public List<AxisPointItem> Points { get; init; } = [];
}

// 多轴动作上下文：用于试教、定位、保存、加载事件参数
public sealed class MultiAxisActionContext
{
    public required string PageName { get; init; }

    public required string PointName { get; init; }

    public Dictionary<string, double> TeachPositions { get; init; } = [];

    public Dictionary<string, double> LocatePositions { get; init; } = [];
}
