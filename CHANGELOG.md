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

## 其它说明

- 若现场 **RelMove** 脉冲时序与默认 **30ms / 100ms** 不一致，可在 `AxisMotionGroupTableControl.cs` 中 `ExecuteRelMoveSignedAsync` 内调整延时
- 主 PLC 参数修改后需重启程序或按现有逻辑重新连接后生效（依当前实现为准）
