# PlcConectCom.WinFormsTest 修改记录

本文档汇总与本工程相关的主要功能与缺陷修复（按主题归类，非严格时间顺序）。

---

## 主 PLC 连接配置外置

- **文件**：`MainPlc.config.json`（与可执行文件同目录，`AppContext.BaseDirectory`）
- **字段**：`IpAddress`、`Port`、`SlaveId`
- **逻辑**：首次运行若不存在配置文件则写入默认值；`HB_HMI_MainForm` 连接 PLC 时读取该配置；连接前做端口/站号等合法性校验（`SanitizeMainPlcConfig`）
- **涉及代码**：`Form1.cs`（`MainPlcConfig`、`LoadMainPlcConfig`、`SaveMainPlcConfig` 等）

---

## 轴组运动表（`AxisMotionGroupTableControl.cs`）

### Excel「导入表格配置」

- 新增按钮 **「导入表格配置」**，选择本地 Excel（表头 **Name / Address / DataType**）
- 将 PLC 导出变量名映射为内部字段键，并按轴行号生成每轴 **MB**（BOOL 一字节一步）、**MD**（REAL 一轴一 MD）、**STRING**（换算为 **MW** 起始）等地址
- 需先在本控件中新增轴行再导入

### 字段模型调整

- **移除**表格中的 **Step+ / Step−** 列（不再作为独立 BOOL 列显示）
- **新增** **当前扭矩**（`CurrentTorque`，REAL）及 Excel 名称片段 **当前扭矩** 的映射
- **新增** **相对位移量**（`RelMoveDistance`，REAL），不在主表显示列，仅在「地址配置」与侧板逻辑中使用
- 加载/设置轴配置时自动剔除历史键 **StepPlus / StepMinus**（`ObsoleteAddressKeys`）

### 主表与右侧「轴控制」面板

- 主表 **不再显示** 所有 **BOOL 控制类** 列（使能、回零、JOG、Abs/Rel、Stop、ABS_Reset 等），仅保留 **状态 BOOL**（●）、**REAL/STRING** 只读列；**手动速度** 不在主表重复显示，由右侧滑条调节
- **Step− / Step+**：向 **`RelMoveDistance`** 写入 **带符号** 的位移（位移下拉为模长，Step+ 为正、Step− 为负），再对 **`RelMove`** BOOL 做 **写 true → 延时 → 写 false** 的脉冲
- 右侧面板：**位移**（可编辑下拉）、**手动速度** TrackBar（防抖写入 `ManualSpeed`）、HOME/STOP/使能/JOG±、使能状态指示灯（读 `Enabling`）
- 操作对象均为 **当前选中行** 的地址字典

### 启动与句柄相关修复

- **不在无句柄时** 对 `DataGridView` 设置 `CurrentCell` / `Selected`（避免主窗体构造阶段异常）
- **`ApplyInitialGridSelectionAndSync`**：在 **`OnLoad`** 及配置加载/ `SetAxes` 后通过 **`BeginInvoke`** 请求执行（已有句柄时）
- **`Dispose`** 中释放手动速度防抖定时器

---

## 主窗体与程序入口（`Form1.cs` / `Program.cs`）

### 轴控件创建时机

- **`AxisMotionGroupTableControl`** 由字段初始化器中的 `new` 改为在 **`HB_HMI_MainForm` 构造函数内、`InitializeComponent()` 之后** 再实例化，避免在 `UIForm` 尚未完成设计器初始化时创建含 `DataGridView` 的复杂子控件导致启动失败

### 启动诊断与授权提示

- **`Program.cs`**：`Application.SetUnhandledExceptionMode(CatchException)`，订阅 **`Application.ThreadException`** 与 **`AppDomain.CurrentDomain.UnhandledException`**，以消息框展示未处理异常
- **授权未通过**时弹出说明框后再退出，避免「无界面静默退出」的误判
- **`Application.Run`** 外层 **`try/catch`**，捕获主窗体构造/运行期异常并提示

---

## 手动操作日志（调试按钮页操作留痕）

- **新增文件**：`ManualOperationLogStore.cs`、`ManualOperationLogPageControl.cs`、`ManualOperationLogPageWindow.cs`
- **存储**：独立目录 `AppContext.BaseDirectory\Logs\ManualOperation\ManualOp_yyyy-MM-dd.db`（SQLite，按日期分库，与运行日志 `Logs\AppLog_*.db` 物理隔离）
- **三类操作**（`ManualOperationCategory`）：
  - `Button` 点击按钮：`AxisMotionGroupTableControl` 的 Step±、JOG±、HOME、STOP、绝对值清零、使能切换、轴停止运动、手动速度（JOG 仅记录按下，松开复位不重复记录）
  - `Position` 修改位置：`PositionParameterTableControl` 的试教、编辑数据（记录每轴写入前/后数值）、保存位置、一键恢复
  - `Output` 点击输出点：`BoolGroupControlTable` 输出点位 ON/OFF 切换（记录地址与开关变化）
- **设备归属**：各控件新增 `DeviceDisplayName`，主 PLC 默认「主 PLC」，子 PLC 由 `BuildChildPlcModule` 传入 `device.Name`
- **操作人**：`ManualOperationLogStore.OperatorProvider`（主窗体注入「当前角色(Windows 账号)」），未注入时退化为系统账号
- **查询入口**：主页面导航新增按钮 **「手动操作日志」**，窗口分三页（点击按钮 / 修改位置 / 点击输出点），支持开始/结束日期区间、今天、最近7天、全部日期、关键字、导出 CSV、打开日志目录
- 写入失败或查询异常一律静默处理，不影响排产与 PLC 操作

---

## 主窗体底部状态栏排版（`StatusStripStyle.cs`）

- **新增文件**：`StatusStripStyle.cs`，把状态栏的字体、颜色、内边距、分隔线、链接样式集中收口，`Form1.ConfigureStatusBar()` 在状态项全部加入后调用一次
- **字体**：全项统一 **微软雅黑 10F**（原为窗体默认宋体 9pt，且各项目混用），`心跳` / `子PLC心跳` 等中英文混排不再出现字号跳变
- **行高**：状态栏高度改由标签上下 padding（各 4px）撑开，实际约 **35px**（原 22px）；不再写死 `StatusStrip.Height`，否则 ToolStrip 项目顶部对齐、文字贴住上边缘
- **间距**：每项左右 padding 12px、`Margin` 清零，分隔线用自定义渲染器画成居中短线（占条高中间 2/3），文字不再挤在分隔线上
- **配色**：语义配色收口为 `OkColor` / `WarnColor` / `ErrorColor` / `MutedColor`；原来的 `Color.LimeGreen` 在浅底上对比度过低，改为深一档绿，闪烁用明暗两相
- **链接**：`PLC: 连接中断` 等链接项改为 **悬停下划线**（`LinkBehavior.HoverUnderline`），常态不再一片下划线
- **缺陷修复**：`ToolStripStatusLabel.IsLink = true` 时 `ForeColor` 不生效，原先「PLC 断开仍显示蓝色」；新增 `StatusStripStyle.SetText()` 同时写 `LinkColor` / `ActiveLinkColor` / `VisitedLinkColor`
- **闪烁抖动修复**：子 PLC 心跳原先靠「加/不加 ●」闪烁，文字宽度每秒变化导致后面几项左右跳动；改为固定文本、仅颜色明暗交替

---

## 导航按钮「PLC 设备管理」文字显示异常修复（`Form1.cs`）

- **现象**：左侧导航里「PLC 设备管理」文字呈宋体小字、细笔画、浅灰，和同排其他按钮（微软雅黑 10F 加粗）明显不一致，看起来像空白按钮
- **原因**：导航外观由 `UpdateNavigationButtonState` 统一套用，但它的按钮数组里**漏了 `_btnPlcDeviceManager`**（该按钮是弹窗入口，不是页面），因此它只拿到 `ApplySunnyUiControlStyles` 的底色，字体仍是 SunnyUI 默认宋体，禁用态下字色又被刷浅
- **修复**：
  - 新增 `GetNavigationButtons()` 作为导航区按钮的**唯一登记处**（含 PLC 设备管理 / 参数修改记录 / 手动操作日志等非页面入口），两处 `UpdateNavigationButtonState` 重载与 `GetUiButtons()` 全部改为复用它
  - 导航外观抽到 `ApplyNavigationButtonVisual()`；字体改为静态复用 `NavigationFont` / `NavigationActiveFont`（原来每次切页 `new Font`，切页频繁会累积 GDI 句柄）
  - 新增 `ForeDisableColor = (108,118,132)`：权限不足被禁用时不再刷成近白灰字，保持不可用但可辨认
- **影响面**：仅导航区按钮外观；权限判定（`_btnPlcDeviceManager.Enabled = role == 管理员`）与点击校验逻辑不变
- **后续调整**：按需要 **「PLC 设备管理」不再出现在导航区**（`Visible = false`，与 `_btnPageMes` 同一处理方式）。
  功能未删：底部状态栏的「PLC 设备管理」链接与「PLC: 连接中断/成功」状态文字点击仍可打开设备管理窗口；
  恢复显示把 `_btnPlcDeviceManager` 的 `Visible` 改回 `true` 即可。同时删掉 `Form1.ResponsiveLayout.cs` 里
  该按钮 42px 的特殊高度分支（隐藏后已无意义）

---

## 产能统计配色：白班蓝 / 夜班灰（`Form1.cs`）

- **图例**（`ComposeProductionColorLegend`）：夜班产量色块由绿色 `(55,193,145)` 改为灰色 `(146,154,168)`
- **周/月堆叠柱**（`ConfigureProductionChartSeries`）：`白班产量` = `ProductionDayShiftColor`(52,103,235)，`夜班产量` = `ProductionNightShiftColor`(146,154,168)
- **日模式小时柱**：`白班小时产量` / `夜班小时产量` 同步为新配色；`当班产量` 折线改中性深灰蓝 `(72,88,112)`，`总产量` 折线橙色不变
- **收口**：四个颜色提为静态常量（`ProductionDayShiftColor` / `ProductionNightShiftColor` / `ProductionCumulativeColor` / `ProductionTotalColor`），以后改色只改一处，避免图例和图表各改一半
- 柱内数值标签仍为深藏青 `(30,45,70)`：在蓝柱上对比度偏低（既有问题），如需改成「深底白字 / 浅底深字」可按背景亮度自动切换

---

## 产能统计柱状图：柱内数值下方标注班次（`Form1.cs`）

- **需求**：白夜班堆叠柱里光看颜色分不清哪段是白班、哪段是夜班，要求在柱内数字下方直接写明「白班 / 夜班」
- **实现**：`RefreshPeriodProductionChart()` 里逐点 `Label` 由纯数字改为 `"数值\n白班"` / `"数值\n夜班"`（MSChart 的 `DataPoint.Label` 支持 `\n` 换行，多行在柱段内居中）
- **未改**：日模式的 24 根小时柱仍只显示数字——单根柱宽约 30~40px，双行标签会左右互相压字；需要的话可改成 9pt 单行或悬停提示
- **验证**：临时 WinForms 工程用同一份 Chart 配置渲染截图，双行标签居中、蓝段显示「白班」、灰段显示「夜班」，`总:xxxx` 标注位于柱顶无冲突；主工程编译 0 错误

---

## 其它说明

- 若现场 **RelMove** 脉冲时序与默认 **30ms / 100ms** 不一致，可在 `AxisMotionGroupTableControl.cs` 中 `ExecuteRelMoveSignedAsync` 内调整延时
- 主 PLC 参数修改后需重启程序或按现有逻辑重新连接后生效（依当前实现为准）
