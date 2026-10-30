# 托盘分组调研与路线比较

> 历史调研。用户最新已确定脱离 Windhawk；文中“两条产品路线待选”不再是当前决策。API 边界与参考源码信息仍可用，最新需求见 [HANDOFF.md](../HANDOFF.md)。

调研日期：2026-10-27。范围：Windows 已有应用托盘图标的文件夹式分组。来源为 Microsoft 官方文档及项目官方仓库；未安装或实测相关产品。

## 1. 结论

产品方向已清楚：点击入口，进入文件夹式分组，再操作原应用图标。技术路线尚未确定。

建议把 **System Tray Folders / Windhawk** 作为原生图标分组的对照基线，再验证独立工具能否在目标系统上可靠操作真实图标。这个顺序是本项目的工程判断，不代表已选定 Windhawk。

主要不确定性在图标接管与事件保真，界面本身相对容易。面板显示应用图标和启动快捷方式，只能证明界面可做，不能证明已经实现托盘收纳。

## 2. 现有能力与相关项目

| 对照方案 | 已查明的能力 | 与需求的关系 |
| --- | --- | --- |
| Windows 自带“^”面板 | 可选择图标是否常驻，也可在任务栏和隐藏区域间拖动 | 已有折叠入口；已查官方说明没有介绍文件夹分组 [S1] |
| System Tray Folders | 在 Windows 11 隐藏图标面板加入文件夹，保留真实图标交互 [S2] | 与核心分组需求最接近；使用原生入口，需 Windhawk，目标机器兼容性未实测 |
| Traymond | 把普通窗口最小化为托盘图标 [S3] | 解决另一类需求，不作为托盘分组实现基础 |
| zserge/tray 等托盘库 | 创建本程序自己的托盘图标和菜单 [S4] | 可用于入口；不等于能管理其他应用的托盘图标 |

System Tray Folders 源码标注 `@include explorer.exe`、`x86-64`、`GPL-3.0-only`；采用 Windows XAML 相关接口。因此只能作为有依据的参考实现，不能仅凭存在该项目就断言兼容本机。若后续复用源码，需保留许可信息并评估分发要求；当前只保存原始参考快照，未整合进自研代码。[S2]

## 3. 公开 API 能做什么

`Shell_NotifyIcon` 负责按图标标识添加、修改、删除托盘图标；这套接口不是现成的“枚举全部图标并放进任意面板”的管理框架。基于已查文档，本项目仍需另行解决其他应用图标的发现与交互。[S5]

`NOTIFYICONDATA` 中有图标所属窗口、ID / GUID 和回调消息。不同图标版本的事件参数不同，同一应用还可以有多个图标。因此不应只按进程名建模，也不能假设向应用发送一次固定消息就等价于所有托盘点击。[S6]

UI Automation 可查询元素支持的控件模式，并按实际支持的模式操作。它为无注入验证提供了候选手段，但具体托盘元素是否公开名称、默认动作和其他行为，必须在目标系统上探测。由此不能推导出“UIA 必然支持全部托盘图标及右键菜单”。[S7]

工程推断：独立路线即使能显示自绘图标，还必须验证真实操作、状态同步、常驻/隐藏及恢复行为；如果最终必须注入或依赖未公开接口，应重新计算维护成本。

## 4. 两条路线比较

下表的工作量与维护判断是相对估计，不是实测工期。

| 维度 | 独立工具 | Windhawk 插件 |
| --- | --- | --- |
| 入口 | 自建托盘入口及面板 | 优先增强原生“^”面板 |
| 文件夹 UI | 自由度高，可做专用管理界面 | 受原生结构限制；已有相近实现 |
| 原图标操作 | 需验证枚举、动作代理、菜单、坐标和状态变化 | 可参考保留原控件的方式；仍需实测 |
| 真正减少原区域杂乱 | 必须验证收纳图标的原位置处理；仅加面板不够 | 可在原生隐藏区域内重排，常驻策略仍需验证 |
| 初期工作量 | UI 中等；可靠兼容层较高且有不确定性 | 相近原型较低；易用的图形管理和兼容仍有工作 |
| 运行形态 | 可独立发布；辅助组件及权限取决于验证结果 | 依赖 Windhawk 及其注入/Hook 机制 [S8] |
| 故障影响 | 若纯外部操作，通常更容易与 Explorer 隔离；若引入注入需重新评估 | 在 Explorer 内运行，故障可能影响任务栏 |
| Windows 更新 | UIA 树及内部接口变化仍可能使功能失效 | 原生结构和内部接口变化可能要求适配 |
| 配置体验 | 易于定制图形化分类管理 | 需区分现有规则设置与本项目所需的可视化管理 |
| 候选技术 | C# / WPF 面板 + Win32 / UIA 验证层；未锁定 [S9] | C++ / WinRT + Windhawk；未锁定 |
| 选择门槛 | 真实图标全链路验证通过，能恢复原状态 | 当前目标 build 通过验证，退出与重启可靠 |

现阶段不承诺独立路线能全程无注入，也不承诺插件覆盖所有 Windows 11 版本。用户对两类入口均接受，因此可优先依据实际可靠性和维护量选择。

## 5. 需要验证的关键问题

1. 能否发现任务栏常驻与隐藏区域的图标？能否正确区分同一应用多个图标？
2. 左键、双击、右键菜单、悬停提示是否保留原行为？
3. 图标刷新、应用重启、提升权限应用、Explorer 重启后会发生什么？
4. 收纳后，原图标是否仍有重复显示？退出或异常时能否恢复正常访问？
5. DPI、多显示器、自动隐藏任务栏及其他任务栏工具是否造成错位或冲突？

详细执行及选择门槛见 [validation-plan.md](validation-plan.md)。这些问题在本次调研中均未实际执行验证。

## 6. 本机环境

只读查询结果：`Win32_OperatingSystem.Caption` 为 Microsoft Windows 11 家庭版 中文版；版本 `10.0.26300`，x64；注册表 DisplayVersion 为 `26H2`，UBR 为 `9711`。

注册表 ProductName 仍显示 Windows 10 字样，与 CIM 名称不同，因此环境记录以系统 Caption 与实际 build 为依据，不只依赖 ProductName 字符串。以上是环境记录，不代表公开发布渠道或插件支持声明。

## 7. 一手来源

- [S1 · Microsoft：Customize the Taskbar in Windows](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-taskbar-in-windows)
- [S2 · 官方 Windhawk mods 仓库：System Tray Folders 源码与内嵌说明](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/system-tray-folders.wh.cpp)
- [S3 · Traymond 官方仓库](https://github.com/fcFn/traymond)
- [S4 · zserge/tray 官方仓库](https://github.com/zserge/tray)
- [S5 · Microsoft：Shell_NotifyIconW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
- [S6 · Microsoft：NOTIFYICONDATAW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)
- [S7 · Microsoft：UI Automation Control Patterns Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview)
- [S8 · Windhawk 官方仓库：Technical details](https://github.com/ramensoftware/windhawk#technical-details)
- [S9 · Microsoft：WPF Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

初次调研链接未锁定提交。后续按用户“下下来看看”的要求，已下载源码并固定提交 `a1190b6c1b631a725f6d628189886d410b3760f6`，同时下载 Windhawk v1.7.3 官方安装包并校验。详见[下载与查看](下载与查看.md)。作者注明在 build 26300 测试过；本机仍未实测。未将社区讨论和搜索摘要当作兼容性测试结果。
