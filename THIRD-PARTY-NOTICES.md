# 第三方参考与开源许可证核查说明 (Third-Party Notices & License Compliance)

## 1. 上游参考项目信息

- **项目名称**：System Tray Folders (Windhawk Mod `system-tray-folders`)
- **作者**：Tetawaves
- **上游版本**：`0.2.3`
- **参考文件位置**：
  - [`references/system-tray-folders/system-tray-folders.wh.cpp`](references/system-tray-folders/system-tray-folders.wh.cpp)
  - [`references/system-tray-folders/UPSTREAM-README.md`](references/system-tray-folders/UPSTREAM-README.md)
- **许可证声明**：`GPL-3.0-only`（见 `system-tray-folders.wh.cpp` 第 13 行 `@license GPL-3.0-only`）

## 2. 本项目的实现关系与许可证合规声明

1. **独立重写（Clean-Room Standalone Architecture）**：
   - `TrayOrganizer` 使用 **C# / WPF (.NET Framework 4.x x64)** 从零独立编写，**未复制或链接 `system-tray-folders.wh.cpp` 的 C++ 源码**，也不依赖 Windhawk 引擎、MinHook 或微软符号服务器（PDB）。
   - 对上游项目的参考限定在交互理念（如 `2×2` 文件夹图标缩略预览、文件夹展开后首格提供 `← 返回` 瓦片、按可执行文件名与 `uID` 进行跨重启身份关联）与 Windows 11 `Taskbar.dll` 托盘系统行为理解。
2. **开源许可证兼容**：
   - 为严格尊重上游 `System Tray Folders` 的 `GPL-3.0-only` 许可精神并保留仓库内随附的参考源码，本仓库可整体遵循 **GPL-3.0-only** 许可证发布与分发，并在本文件中完整保留原作者 Tetawaves 的署名与许可声明。
