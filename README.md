
[README.md](https://github.com/user-attachments/files/35312230/README.md)# HB_HMI 上位机（PlcConnectTest）

面向工业设备的 WinForms HMI / SCADA 上位机解决方案：以 Modbus TCP 与 PLC 通信为核心，覆盖轴运动调试、参数与报警管理、工站控制、产能统计，并集成 MES/SCADA、机器视觉（海康 / 大华相机）、电磁焊、客户看板等外围模块。

- 主工程：`PlcConectCom.WinFormsTest`，程序集名 / 产物名 `HB_HMI_V1.26.11.11`
- 解决方案：`PlcConectCom.WinFormsTest.sln`（完整工程）
- 当前版本依据：2026-10-20 ~ 2026-11-11 的修改记录与工程内 CHANGELOG

> **关于本目录**：这是带 `- 副本` 后缀的工作副本，只保留了部分源码和文档，缺少 `.sln`、程序入口、若干工程文件与预编译 DLL，**在本目录直接 `dotnet build` 无法通过**。本文按「完整解决方案」归纳架构，并在「构建与运行」一节列出副本缺失项。

---

## 一、技术栈

| 类别 | 选型 |
| --- | --- |
| 框架 | .NET 8（`net8.0-windows`）+ Windows Forms |
| UI | SunnyUI 3.9.6（窗体 / 表格 / 页签）、`System.Windows.Forms.DataVisualization`（图表） |
| PLC 主站 | NModbus 3.0.81（`PlcConectCom`） |
| Modbus 从站 | HslCommunication 13.0.3（仅 `MesScada` / 内置 ModbusTCP 服务端） |
| 数据存储 | `Microsoft.Data.Sqlite` 10.0.7（按日分库的日志 / 报警历史）+ 程序目录 JSON 配置 |
| 其它 | ExcelDataReader 3.7.0（Excel 导入配置）、System.IO.Ports（串口）、System.Management（机器码）、RSA-SHA256 授权 |
| 目标平台 | `AnyCPU;x86`，硬件 SDK 为 32 位，现场产物固定 x86 |

---

## 二、解决方案结构

完整解决方案含 7 个项目，`MesScada` 最初为独立工程、现已被主程序引用。

| 工程 | 类型 | 职责 | 当前副本状态 |
| --- | --- | --- | --- |
| `PlcConectCom.WinFormsTest` | WinExe 主程序 | 主窗体 `HB_HMI_MainForm`、导航与权限、PLC 连接 / 心跳、全部业务页面装配 | 主要源码在，缺 `Program.cs`、`Form1.Designer.cs`、部分页面宿主窗体 |
| `PlcConectCom` | 类库 | `PlcCommunicator` / `AddressParser`：Modbus TCP 异步读写、MX/MB/MW/MD 地址解析 | 仅剩 `README.md` |
| `PlcConectCom.WinForms.Controls` | 类库 | PLC 绑定控件 `PlcBoundButton` / `PlcBoolSwitchControl` / `PlcBoolLabe` | 3 个 `.cs`，缺 `.csproj` |
| `HB_CameraUI` | 类库 | 海康 `THCamSetHostForm` 与大华 `CamSettingNew_DaHua` 两工程、标定 / 精度验证、自动拍照、`FailTip` 报警弹窗 | 源码在，缺预编译 `FuncBlock.dll` / `TCPLib.dll` |
| `CustemerUI` | 类库 | 小料机客户看板（订阅小料 / 垫板定位结果，可嵌入主页面） | 源码在 |
| `DianCiHan` | 类库 | 电磁焊流程、焊头参数下发、前/中/后三组运行器、自动流程日志 | 源码在，缺 `HeatDriver.dll` / `SerialPortLib.dll` |
| `MesScada` | 类库 | MES/SCADA 信号与工位状态、配置分页、Modbus TCP 从站 | 源码在 |
| `HeatDriver` | 外部工程 | 焊头硬件驱动（预编译 DLL，通过 HintPath 引用） | **完全缺失** |
| `BlazorUI` | Web（旁路） | Blazor 多 PLC 采集看板 / 单点读写（见 `MULTI_PLC.md`） | 仅 `wwwroot/favicon.png` |

依赖关系：

```text
PlcConectCom.WinFormsTest（主程序）
  ├─ ProjectReference → CustemerUI / WinForms.Controls / DianCiHan / MesScada / HB_CameraUI
  └─ HintPath DLL    → PlcConectCom.dll / NModbus.dll

CustemerUI  → PlcConectCom / WinForms.Controls / HB_CameraUI
DianCiHan   → PlcConectCom / HB_CameraUI / HeatDriver.dll + SerialPortLib.dll
HB_CameraUI → PlcConectCom / FuncBlock.dll + TCPLib.dll
MesScada    → HslCommunication（NuGet，输出需 CopyLocalLockFileAssemblies）
```

---

## 三、目录结构

```text
PlcConnectTest - 副本/
├── PlcConectCom.WinFormsTest/    主程序（Form1.cs 约 6000+ 行为共享内核，高风险区）
│   ├── Form1.cs / Form1.ResponsiveLayout.cs / StatusStripStyle.cs
│   ├── *Control.cs               轴组、位置参数、参数表、报警、工站、通讯、日志等控件
│   ├── docs/project-structure.html  交互式项目结构图 / 改模块风险指引
│   └── CHANGELOG.md
├── PlcConectCom/                 PLC 通讯类库（副本仅剩 README.md）
├── PlcConectCom.WinForms.Controls/  PLC 绑定控件
├── HB_CameraUI/HB_CameraUI/HB_CameraUI/  相机工程（大华按 partial 拆 5 个文件）
│   └── ../DAHUA_INTEGRATION.md
├── DianCiHan/                    电磁焊（Options / Plc / Process / Ui 分层）
├── MesScada/                     MES（Abstractions / Data / ModBus / Scada / Ui 分层）
├── CustemerUI/CustemerUI/        客户看板
├── BlazorUI/wwwroot/             Blazor 采集看板残留
├── MULTI_PLC.md                  多 PLC 使用说明（WinForms + Blazor）
└── 修改记录_*.txt                全项目 / 分阶段修改记录（含部署要点、遗留事项）
```

---

## 四、核心功能

### 4.1 主界面与权限

左侧导航（`Form1.cs` 中登记）：主页面、系统配置、调试按钮、工站控制、电磁焊、相机调试、状态监控、记录查询、参数调整、参数修改记录、手动操作日志、产能统计、客户界面。**PLC 设备管理**与 **MES** 两个入口已从导航隐藏（功能保留，分别从状态栏链接、后台自启打开）。

- 三级角色：`管理员 / 工程师 / 作业员`，升权需密码，作业员仅可访问主页面和记录查询
- **无操作自动降级**：10~60 秒可配（默认 50 秒），只认本程序窗口内的鼠标 / 键盘 / 触摸操作，模态弹窗期间暂停计时
- 启动门禁：`LicenseAuthorization.cs` 用机器码 + RSA-SHA256 校验 `%ProgramData%\PlcConectCom\license.key`

### 4.2 PLC 通信与地址制式

私有地址制式 `MX / MB / MW / MD`，统一映射到 Holding Register：

| 类型 | 格式 | 映射规则 |
| --- | --- | --- |
| MX | `MX100.0` | 位，按 `字节偏移 / 2` 定位 MW，位号落低/高 8 位 |
| MB | `MB200` | 每 2 个 MB 映射 1 个 MW |
| MW | `MW300` | 直接映射 Holding Register |
| MD | `MD400` | 1 个 MD 映射连续 2 个 MW（起始偏移 = `MD偏移 × 2`） |

换算：`1 MD = 4 MB = 2 MW = 32 MX`；`1 MW = 2 MB = 16 MX`；`1 MB = 8 MX`。支持 `bool / short / double / string` 的单值与批量异步读写。

上层能力：

- **多 PLC**：`PlcDevices.config.json` 管理设备（名称 / IP / 端口 / 站号 / 心跳地址），每台独立连接；主 PLC 保留原功能区，子 PLC 按设备分页并独立存储配置，兼容旧 `MainPlc.config.json`
- 子 PLC 心跳、自动运行信号、`PLC 版本号`（MW 起始 ASCII 字符串，最多 30 字符）每秒轮询
- 内置 **Modbus TCP 服务端**（FC01/02/03/04/05/06/15/16），数据来源支持本地地址区或桥接 PLC，带地址区查看器
- 报警点位用 `PlcBoolBatchReader` 聚合成连续块批量读，轮询 500ms

### 4.3 业务模块

| 模块 | 要点 |
| --- | --- |
| 轴组控制 | 多轴 / 同步轴运动、Step± 带符号位移、JOG / HOME / STOP 同步、100ms 脉冲、编辑数据 8 字段并写后回读校验、支持 Excel 导入地址 |
| 位置参数 | 表格定义本地存、数值实时读 PLC、保存快照 / 一键恢复（5 分钟自动快照、防全 0 污染） |
| 参数表 / 参数调整 | 三张参数表 + 配方 + 变更记录 + 关键字，REAL 统一 3 位小数，BOOL 下拉 1/0 |
| 报警 | 报警配置 / 弹窗配置（主 PLC 与各子 PLC 独立）、主页面合并实时报警、Top3 日期区间统计、SQLite 报警历史 |
| 工站 | 状态字典 Excel 多工作表、结构体地址自动分配（`MD1500 + 20×工站号`）、状态控件主页嵌入与弹窗 reparent |
| 任务流水线 | `TaskPipelineRunner` 绑定 PLC / 通讯 / 报警触发源，高风险跨域模块 |
| 产能统计 | 按「生产日」统计（夜班归结束日）、白班蓝 / 夜班灰堆叠柱、日/周/月视图 |
| 手动操作日志 | 点击按钮 / 修改位置 / 点击输出点三类留痕，记录设备归属与操作人，支持日期区间、关键字、导出 CSV |

### 4.4 外围模块

- **MES / SCADA**：从 XLW `net452` 迁移，XCore 依赖收口为 `IMesDeviceContext` 由宿主注入；17 个信号地址 + 轮询周期 + Modbus 端口可配，保存前做格式与重复地址校验
- **相机**：海康 / 大华两工程，选择存 `camera.project.selection.json`；大华自动流程后台常驻、PLC 信号地址可配（36 条）、XSetting 标定体系、拍照握手与补偿回写，切换工程需等待旧线程退出
- **电磁焊**：焊头参数下发（0x101~0x108 寄存器）、三组独立流程、允许信号模拟、连续 N 次才弹报警（默认 5）、前/中/后三组日志页签
- **客户看板**：订阅 `XiaoLiaoCamResult` / `DianBanCamResult`，适配 1024×768

### 4.5 日志体系

| 日志 | 位置 |
| --- | --- |
| 运行日志 | `Logs/AppLog_yyyy-MM-dd.db`（界面只保留最近 1000 行） |
| 手动操作日志 | `Logs/ManualOperation/ManualOp_yyyy-MM-dd.db`（独立库） |
| 报警历史 | 程序目录 SQLite |
| 相机日志 | `Logs/CameraLog{yyyyMMdd}.txt` |
| MES 日志 | `Logs/Mes/`（按天） |
| 自动流程日志 | 电磁焊按前/中/后组分开 |

---

## 五、配置与数据文件

所有运行时配置落在 `AppContext.BaseDirectory`（程序目录），首次运行写默认值；子 PLC 配置以 `storageKey` 派生 `Xxx.{key}.config.json`。

| 分类 | 主要文件 |
| --- | --- |
| 连接 | `MainPlc.config.json`、`PlcDevices.config.json`、`plc.connection.json` |
| 主页面 | `MainPageDisplay.config.json`（分辨率缩放、自动运行地址、PLC 版本号地址、降级时限、Top3 区间）、`MainButtonArea.layout.json` |
| 轴 / 位置 | `AxisMotionGroup.config.json`、`PositionParameterTable.config.json` + `.snapshot.json` |
| 参数 | `ParameterTable.config.json` + `.records.json` / `.recipes.json` / `.keywords.json`、`ParameterAdjust.keywords.json` |
| 报警 | `AlarmAddress.config.json`、`AlarmPopup.config.json`、`ManualOperationPrompt.config.json` |
| 按钮 / 显示 | `MainPlcHorizontalButtons.config.json`、`BoolGroupControlTable.config.json`、`BoolSignal*.json` |
| 工站 / 通讯 | `StationOperation.config.json`、`CommunicationInstances.config.json`、`ModbusWatch.config.*.json` |
| MES / 相机 | `MesScada.config.json`、`MesSetting.config.json`、`MesYield.json`、`camera.project.selection.json`、`CameraThreadStartup.config.json`、`SettingOption.dahua.json`、`SettingCalibration.xml`、`TH_CamSet.dahua-plc-signals.json` |
| 电磁焊 | `DianCiHan.json` + `DianCiHan.snapshot.json` |
| 权限 | `RoleAuth.config.json`、`%ProgramData%\PlcConectCom\license.key` |

> 新增配置字段缺失时按默认值处理；**部署时不要覆盖现场 `*.config.json`**（密码、PLC 地址、降级时限、报警与按钮配置等）。

---

## 六、构建与运行

**完整工程**（Windows + .NET 8 SDK）：

```bash
dotnet build PlcConectCom.WinFormsTest.sln -p:Platform=x86
```

历史构建状态：0 错误 / 3 个既有警告（CS8600 / CS0108）。

**当前副本缺失清单**（补齐后才能在原解决方案里编译）：

- 根目录 `PlcConectCom.WinFormsTest.sln`
- `PlcConectCom/PlcConectCom.csproj` 及全部源码；`PlcConectCom/bin/Debug/net8.0/` 下的 `PlcConectCom.dll` / `NModbus.dll`
- `PlcConectCom.WinForms.Controls/PlcConectCom.WinForms.Controls.csproj`
- 主程序 `Program.cs`（当前源码中没有 `Main` 入口）、`Form1.Designer.cs`、`.resx`、多个 `*PageWindow.cs` / `AppDialog.cs` / `ApplicationLogStore.cs` 等文件
- `HeatDriver` 工程及其 `HeatDriver.dll`、`SerialPortLib.dll`
- `HB_CameraUI/.../bin/Debug/net8.0/` 下的 `FuncBlock.dll`、`TCPLib.dll`
- `BlazorUI` 源码与 `appsettings.json`；`工具集/DahuaCameraChecks` 回归检查工程

构建注意：

- `DianCiHan` 通过 HintPath 引用预编译 `HeatDriver.dll`，改驱动源码后必须手动重编该 DLL，否则会静默使用旧版
- 库项目设 `CopyLocalLockFileAssemblies=true`，保证 `HslCommunication.dll` / `SunnyUI.dll` 输出到主程序目录
- 手写 partial 布局文件不要带同名 `.resx`，否则 manifest 名撞车触发 `MSB3577`

---

## 七、部署要点

只改主工程时的最小更新集（从 `bin/Debug/net8.0-windows` 拷贝）：

```text
HB_HMI_V1.26.11.11.exe
HB_HMI_V1.26.11.11.dll
HB_HMI_V1.26.11.11.runtimeconfig.json
HB_HMI_V1.26.11.11.deps.json
```

- .NET 8 的 exe 只是启动器，业务代码在同名 dll 里，只拷 exe 等于没更新
- 依赖库（`HB_CameraUI` / `DianCiHan` / `MesScada` / `PlcConectCom` 等）有改动时，必须连同对应 dll 一起更新
- 程序集名变更后产物文件名变化，设备快捷方式 / 开机自启需指向新 exe
- 保留现场 `*.config.json` 与 `Logs` 目录

---

## 八、已知问题 / 技术债

- `PlcCommunicator` 无 TCP 收发超时、`ConnectAsync` 无取消令牌、信号量无超时，PLC 离线时读写可能长时间阻塞（影响退出与界面响应）
- 运行日志入库仍在 UI 线程同步写 SQLite，日志密集时有轻微顿挫，可移到后台线程
- `Form1.cs` 为 6000+ 行巨型窗体；`CamSettingNew_DaHua.cs` 亦为大体量文件
- 7 个 `*PageWindow` 的 `AttachPage` 实现同构复制，可抽公共基类
- 相机 `GetCamoffset` 已对齐参考工程算法，建议现场复核一次实际补偿结果
- 大华 `FailTip` 在后台宿主线程弹窗（TopMost），无人值守需人为确认
- PLC 版本号字符串按低字节在前解析，若现场显示错位需调字节序
- 产能统计柱内数值标签为深藏青，在蓝柱上对比度偏低

---

## 九、文档索引

| 文档 | 内容 |
| --- | --- |
| `MULTI_PLC.md` | 多 PLC 使用说明（WinForms HMI + Blazor 看板） |
| `PlcConectCom/README.md` | 通讯类库 API、地址格式与读写示例 |
| `PlcConectCom.WinFormsTest/CHANGELOG.md` | 主工程功能与缺陷修复（按主题归类） |
| `HB_CameraUI/DAHUA_INTEGRATION.md` | 大华相机工程启动/切换、握手地址与回归检查 |
| `PlcConectCom.WinFormsTest/docs/project-structure.html` | 交互式项目结构图、模块边界与改动风险等级 |
| `修改记录_全项目_2026-10-20至11-11.txt` | 全量变更、模块索引、部署要点、遗留事项 |
| `修改记录_2026-11-04至11-11.txt` | 权限降级、版本号显示、日志卡顿等专题详述 |

---

*本 README 依据目录内源码、工程文件与修改记录归纳，整理于 2026-11-24。*
