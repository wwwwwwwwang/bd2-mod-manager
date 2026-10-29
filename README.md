# BD2ModManager

**《棕色尘埃2》的一键皮肤解锁工具。**

> 下载一个 exe，打开开关即可：自动安装 BepInEx 加载器和内置皮肤解锁插件。
>
> 仅让已拥有服装的皮肤在本地显示；不修改游戏数值，不联网。

![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
[![Release](https://img.shields.io/github/v/release/wwwwwwwwang/bd2-mod-manager?label=release&sort=semver)](https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest)

## ✨ 核心特性

- **一键启用**：打开开关，自动安装或更新 loader 与内置解锁插件。
- **一键还原**：关闭开关，移除本工具写入的文件并还原被覆盖的原文件。
- **只解锁已有服装**：不凭空生成服装，不修改攻击、货币、关卡等数值。
- **单文件开箱即用**：免安装、不需要 .NET、不需要管理员权限。
- **开源可审计**：解锁插件为 MIT 实现，构建和依赖来源可查。

## 🚀 快速开始

1. 前往 [Releases](https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest) 下载 `BD2ModManager-<版本>-win-x64.exe`。
2. **完全退出游戏**，双击运行（无需安装）。
3. 确认游戏目录指向含 `BrownDust II.exe` 的文件夹；首次没找到会自动展开「目录与环境」，也可以点「选择目录」。
4. 打开「一键启用」，等待状态显示「已启用」后启动游戏。

> **关闭开关**：会移除内置解锁插件和本工具安装的 loader，并清理运行时产物。
>
> **系统要求**：Windows 10 / 11 x64；支持官网启动器安装版。Google Play Games 版结构不同，不支持。BD2 不在 Steam。

## 📦 发布产物

Release 只需要下载一个文件：

| 文件 | 说明 |
|---|---|
| `BD2ModManager-<版本>-win-x64.exe` | 唯一需要下载的文件；loader 与解锁插件都已内嵌，双击即用 |

```text
BD2ModManager-<版本>-win-x64.exe
├─ bd2-loader（内嵌）
└─ BD2SkinUnlock.dll（内嵌）
```

> `SHA256SUMS.txt` 仅用于校验，不需要下载。

## ❓ 常见问题

**会被杀毒软件报毒吗？**

无签名 exe 写入 DLL 容易被误报。项目开源、不混淆、不上壳，你可以从源码自行构建；发布包同时提供 `SHA256SUMS.txt` 供核对。

**会修改游戏本体吗？**

不会。只写入 loader 和插件所需文件；关闭开关会还原。

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

本工具与 Brown Dust 2 运营方无关；使用本工具产生的一切后果由使用者自行承担。

## 署名

**燧人氏** —— 上古之人，观鸟啄燧木而得火，遂钻木取火，教民熟食，以御寒夜、避腥臊。
火种自此不再是天赐的偶然，而是人人可以自己点起的东西。燧人氏不曾把火据为己有，
也不问来取火的人是谁。

这个工具想做的事也一样：不收费、不预置闭源插件、不抹除任何人的署名，
只把本该属于公共领域的东西，干净地交回每一个使用者手上。
