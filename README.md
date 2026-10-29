# BD2ModManager

**《棕色尘埃2》的极简、安全、开源 BepInEx 加载器与插件管理器。**

> 一键安装 / 卸载 / 开关插件，绝不预置任何闭源组件。
>
> 插件由你提供，工具也只删除自己安装或生成的文件。

![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
[![Release](https://img.shields.io/github/v/release/wwwwwwwwang/bd2-mod-manager?label=release&sort=semver)](https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest)

## ✨ 核心特性

- **傻瓜式管理**：一键安装 / 卸载 loader，单独开关每个插件，不需要命令行。
- **第三方组件**：把解压后的插件目录整包装进来，可整包开关、整包移除。
- **极致安全**：清单驱动卸载、逐文件 SHA256 校验、严格隔离用户文件；改过的文件一律保留。
- **开箱即用**：独立 GUI 单文件，免安装、不需要 .NET、不需要管理员权限。
- **内置开源插件**：自研 MIT 皮肤解锁插件，仅让已拥有服装的皮肤在本地显示（[详见文档](docs/PLUGIN_SKIN_UNLOCK.md)）。

## 🚀 快速开始

1. 前往 [Releases](https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest) 下载 `BD2ModManager-<版本>-win-x64.exe`。
2. **完全退出游戏**，双击运行（无需安装）。
3. 确认游戏目录指向含 `BrownDust II.exe` 的文件夹；没自动找到就点「选择目录」。
4. 打开「启用 Mod 支持」，状态显示「已启用」后即可启动游戏。

需要插件时：

- 点 **安装内置插件**，装上自带的皮肤解锁插件；
- 或点 **安装组件…**，选择解压后的插件目录（含 `.dll`，也可直接选 `BepInEx` 的上一层），工具会把它装成独立组件；
- 直接把 `.dll` 放进 `BepInEx/plugins` 也可以，GUI 会在插件列表里单独开关。

> **卸载 / 还原**：关闭「启用 Mod 支持」即可删除本工具写入的文件、还原被覆盖的原文件，并清理 loader 运行时产物。`plugins` 里你自己放的插件会保留。
>
> **系统要求**：Windows 10 / 11 x64；支持官网启动器安装版。Google Play Games 版结构不同，不支持。BD2 不在 Steam。

## 📦 发布产物

Release 只需要下载一个文件：

| 文件 | 说明 |
|---|---|
| `BD2ModManager-<版本>-win-x64.exe` | 唯一需要下载的文件；loader 包与内置解锁插件都已内嵌，双击即用 |

```text
BD2ModManager-<版本>-win-x64.exe
├─ bd2-loader（内嵌）
└─ BD2SkinUnlock.dll（内嵌）
```

> `SHA256SUMS.txt` 仅用于校验，不需要下载。

## ❓ 常见问题

**会被杀毒软件报毒吗？**

无签名 exe 写入 DLL 容易被误报。项目开源、不混淆、不上壳，你可以从源码自行构建；发布包同时提供 `SHA256SUMS.txt` 供核对。

**需要管理员权限吗？**

不需要。只要游戏目录可写即可。

**支持哪些游戏版本？**

官网启动器安装的 PC 版。Google Play Games 版目录结构不同，工具会识别并拒绝。

## 🛠️ 进阶与开发者文档

普通玩家不需要读这些；想了解底层实现、安全机制或自行编译时再看：

- [安全设计与 13 条硬约束](docs/SECURITY.md)
- [架构与代码结构](docs/ARCHITECTURE.md)
- [构建与发布流程](docs/BUILD.md)
- [内置皮肤解锁插件](docs/PLUGIN_SKIN_UNLOCK.md)
- [开源协议与第三方合规](docs/COMPLIANCE.md)

想参与贡献？先读架构与构建文档，改完跑两套测试，再提 PR；也可以在 [Issues](https://github.com/wwwwwwwwang/bd2-mod-manager/issues) 反馈问题。

## 🙏 致谢 & 协议

本项目基于 [MIT](LICENSE) 协议开源，第三方组件遵循各自原协议，详见 [COMPLIANCE.md](docs/COMPLIANCE.md)。

感谢 BepInEx、HarmonyX、Avalonia 以及所有开源社区贡献者。

本工具与 Brown Dust 2 运营方无关；使用 Mod 相关功能产生的一切后果由使用者自行承担。

## 署名

**燧人氏** —— 上古之人，观鸟啄燧木而得火，遂钻木取火，教民熟食，以御寒夜、避腥臊。
火种自此不再是天赐的偶然，而是人人可以自己点起的东西。燧人氏不曾把火据为己有，
也不问来取火的人是谁。

这个工具想做的事也一样：不收费、不预置闭源插件、不抹除任何人的署名，
只把本该属于公共领域的东西，干净地交回每一个使用者手上。
