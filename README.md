# Windows 独立系统托盘收纳工具 (`TrayOrganizer`)

独立运行的 Windows 11 托盘图标收纳工具：把任务栏托盘里的图标收进自己的文件夹，原应用的左键、双击、右键菜单照常可用。不依赖 Windhawk，不向 `explorer.exe` 注入 DLL。

---

## 1. 功能概览

### 界面

界面遵循 Windows 11 的 Fluent 风格（WPF，无第三方库），文字为简体中文，图标使用 Segoe Fluent Icons。

- **主界面（设置式窗口，[`MainWindow`](src/TrayOrganizer/UI/MainWindow.xaml)）**：窗口背景为 Mica，左侧是导航栏，右侧是内容区。
  - 导航：全部图标 / 已收纳 / 未分组 / 各个文件夹 / 设置。
  - 图标列表：搜索框（匹配应用名、文件名、提示文字）、多选后点击“收纳到”或“移出收纳”、每个图标右侧的“更多操作”菜单（移动到文件夹、移出收纳、模拟单击 / 双击、打开应用原生菜单）。列表按增量更新，不会因为每秒刷新而丢失悬停、滚动位置和选择状态。
  - 文件夹：新建、重命名、删除、上移 / 下移（右键侧栏文件夹）。图标可以拖到侧栏文件夹上归类。
  - 设置页：原位处理方式（完全隐藏 / 仅从任务栏降级到溢出区）、暂停收纳、拖入时自动打开收纳面板、开机自动启动、打开配置目录、恢复全部图标并退出，以及运行日志。
- **托盘浮层（Acrylic，[`FlyoutWindow`](src/TrayOrganizer/UI/FlyoutWindow.xaml)）**：点击本工具的托盘图标，在点击位置附近弹出，只显示文件夹和正在运行的图标；再次点击托盘图标会收起。外部拖拽图标时也可自动弹出。
- **托盘右键菜单**：原生菜单，包含打开收纳面板、打开管理界面、暂停收纳、恢复全部图标并退出。
- **主题**：跟随系统浅色 / 深色模式和强调色，切换时实时更新；托盘图标按任务栏明暗自动换成黑色或白色。
- **DPI**：通过应用清单声明 Per-Monitor V2，多显示器、不同缩放比例下坐标和渲染保持一致。

### 核心行为

1. **从真实托盘拖入**：按住任务栏常驻区或 `^` 溢出面板里的真实图标，拖到收纳浮层或主界面的文件夹上完成绑定（`WH_MOUSE_LL` 全局鼠标钩子 + `Shell_NotifyIconGetRect` 命中测试）。
2. **持久绑定**：绑定规则按 GUID、注册表键、可执行文件路径 + `uID`、文件名 + `uID` 四级匹配，不依赖会变化的提示文字；应用重启或本工具重启后自动恢复归属。
3. **原位隐藏与恢复**：收纳后通过 `Shell_NotifyIconW(NIM_MODIFY, NIS_HIDDEN)` 和 `NotifyIconSettings\IsPromoted` 收起原图标。正常退出时全部恢复。
4. **资源管理器重启恢复**：监听 `TaskbarCreated` 广播；收到后等待约 1.5 秒，重新扫描托盘，重新对已收纳图标执行隐藏（Shell 重建后会丢失隐藏状态），并重新提升本工具自己的托盘图标。WinForms `NotifyIcon` 会自行重新添加图标。
5. **自适应轮询**：主界面或浮层可见、或正在拖拽时每 1 秒读取一次 `explorer.exe` 内存；窗口都隐藏时降为每 3 秒；检测到图标增减或应用重启后，短时间内回到 1 秒。
6. **安全恢复**：正常退出自动恢复；独立 `--watchdog` 伴随进程在主进程被强杀或崩溃后自动恢复全部隐藏图标；也可手动运行 `restore-tray.bat` 或 `--restore-all`。

---

## 2. 与上游 `System Tray Folders`（Windhawk Mod）的对照

| 功能环节 | 上游 (依赖 Windhawk) | 本项目 |
| :--- | :--- | :--- |
| **宿主方式** | 需要安装 Windhawk，向 `explorer.exe` 注入 C++ Mod | 独立进程 `TrayOrganizer.exe`，不注入 `explorer.exe` |
| **符号依赖** | 依赖微软符号服务器下载 `Taskbar.dll` PDB | 不依赖 PDB：结合 `HKCU\Control Panel\NotifyIconSettings` 与 `Taskbar.dll` 的 `NotifyIconItem` 内存特征定位 |
| **图标枚举** | Hook `TrayUI::CheckStagingTray` | [`TrayIconEngine`](src/TrayOrganizer/Core/TrayIconEngine.cs) 跨进程只读扫描 `explorer.exe` 中的 `NotifyIconItem` |
| **原位隐藏** | Hook 内部 `OverflowFlyoutModel` | [`TrayActionDispatcher`](src/TrayOrganizer/Core/TrayActionDispatcher.cs) 调用公开的 `Shell_NotifyIconW` 与注册表 `IsPromoted` |
| **点击与右键菜单** | 修改 XAML 视图的回调 | 向图标所属 `HWND` 投递回调消息（兼容 `NOTIFYICON_VERSION_4` 坐标打包、传统 `#32768` 菜单、Electron 的 `WM_CONTEXTMENU`） |
| **外部拖入** | 仅支持 `^` 面板内的整理模式 | [`ExternalTrayDragMonitor`](src/TrayOrganizer/Core/ExternalTrayDragMonitor.cs) 支持从任务栏常驻区或 `^` 面板直接拖入 |

---

## 3. 构建、运行与恢复

### 构建

无需额外 SDK，直接使用系统自带的 .NET Framework 4.x `MSBuild.exe`：

```powershell
.\build.bat
# 或：
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" "src\TrayOrganizer\TrayOrganizer.csproj" /p:Configuration=Release /nologo /v:minimal
```

产物位于 `dist\TrayOrganizer\`：`TrayOrganizer.exe`（已嵌入 Per-Monitor V2 清单）和 `TrayOrganizer.exe.config`（WPF 多显示器缩放开关，需与 exe 放在一起）。

### 命令行参数

| 参数 | 作用 |
| :--- | :--- |
| （无） | 启动主界面和后台收纳；[`run.bat`](run.bat) 同样如此 |
| `--minimized` | 只驻留托盘，不显示主界面（开机自启使用） |
| `--restore-all` | 按上次保存的状态恢复全部被隐藏的图标后退出；也可双击 [`restore-tray.bat`](restore-tray.bat) |
| `--auto-verify` | 运行自动化实测流程（拖入、绑定、原生菜单、暂停 / 恢复），结果写入日志，完成后自动退出 |
| `--theme=dark` / `--theme=light` | 仅供测试：强制界面使用深色或浅色主题，忽略系统设置 |
| `--watchdog <PID>` | 内部使用：等待指定主进程退出，若有遗留的隐藏状态则自动恢复。主进程启动时会自动拉起 |

同一时间只运行一个实例；再次启动会唤起已运行实例的主界面（`--minimized` 除外）。

### 配置与日志

- 配置目录：`%LOCALAPPDATA%\TrayOrganizer\`，包含 `config.json`、`active_hidden_state.json`（运行期隐藏图标账本，正常退出后清除）和 `verification.log`。
- 日志先写入内存队列，约每 500 毫秒批量落盘，退出和恢复时会强制写出；单个日志文件超过约 2 MB 时滚动为 `verification.old.log`。
- 工作区镜像（可选）：如果 `dist\TrayOrganizer\` 往上两级存在 `data` 目录（即本项目的 [`data`](data) 文件夹），配置、隐藏状态和日志会同步写一份到那里。该目录不存在时不会创建，也不会镜像。

---

## 4. 已验证的目标图标（开发机记录）

以下三个应用用于 `--auto-verify` 实测，路径为作者机器上的实际值，仅作参考。

| 目标 | 可执行文件 | `uID` | `uCallbackMessage` | 窗口类 | 识别与菜单特征 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **流量监控** | `TrafficMonitor.exe` | `0` | `0x7ED` | MFC 窗口 | 提示文字随网速变化，依靠注册表键 + 路径 + `uID` 绑定；弹出原生 `#32768` 菜单 |
| **Token 监控** | `Token Monitor.exe` | `3` | `0x8001` | `Electron_NotifyIconHostWindow` | 初始提示为空；发送一次 `WM_CONTEXTMENU` 弹出 Electron 菜单 |
| **PadMouse** | `PadMouse.exe`（或 `pythonw.exe` 运行态） | `0` | `0x40B` | `PadMouse...SystemTrayIcon` | 通用宿主进程（`pythonw.exe`）会额外比对提示文字前缀；弹出原生 `#32768` 菜单 |

---

## 5. 已知限制

1. **`Taskbar.dll` 内存结构**：偏移量已在 Windows 11 `10.0.26300.9711` 上核实，并内置了基于注册表特征的动态搜索作为回退；若未来大版本改动 `NotifyIconItem` 布局，需要更新偏移表。
2. **管理员权限（UIPI）**：被收纳的应用以管理员权限运行时，`Shell_NotifyIconW` 隐藏 / 恢复和 `PostMessage` 回调转发在实测中可用；但目标应用若拒绝非提升进程激活其窗口，菜单失焦关闭可能需要点击菜单外部。
3. **许可**：上游参考项目 `System Tray Folders` 采用 `GPL-3.0-only`，详见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

更多背景与交接说明见 [`HANDOFF.md`](HANDOFF.md) 和 [`docs`](docs) 目录。
