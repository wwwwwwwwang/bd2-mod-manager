# 开源协议与第三方合规

> 本仓库自写代码以 **MIT** 开源；随包分发的第三方组件保持其原有许可。
> 本文是发布包的合规说明，不构成法律意见。

## 一、本仓库的许可

`LICENSE` 采用 **MIT License**，覆盖本仓库自行编写的：

- `src/BD2ModManager.Core/`
- `src/BD2ModManager.Cli/`
- `src/BD2ModManager.Gui/`
- `src/BD2ModManager.SkinUnlock/`
- `scripts/`
- `tests/`
- 构建配置（`Directory.Build.props`、`.github/workflows/` 等）

版权声明：Copyright (c) 2026 燧人氏 (Suirenshi)。

MIT 允许使用、复制、修改、合并、发布、再许可与销售，但必须保留版权声明与许可声明；软件按「原样」提供，不附带任何担保。

## 二、随 loader 包分发的第三方组件

| 组件 | 版本 | 许可 | 能否随工具分发 | 合规要点 |
|---|---|---|---|---|
| BepInEx | 5.4.23.6 | **LGPL-2.1** | ✅ | 附许可证全文 + 源码链接；保持 DLL 可替换 |
| HarmonyX / MonoMod / Mono.Cecil | 随 BepInEx 官方包 | **MIT** | ✅ | 官方包内自带版权声明 |

> 0.4.x 旧版发布包曾包含 BepInEx.ConfigurationManager 19.1（LGPL-3.0）；后续版本不再随包分发。

### BepInEx 5.4.23.6（LGPL-2.1）

- 项目：<https://github.com/BepInEx/BepInEx>
- 源码：<https://github.com/BepInEx/BepInEx/tree/v5.4.23.6>
- 发行版：<https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6>
- 发行包 SHA256：`6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`
- 许可证全文：`scripts/loader-assets/LICENSE-BepInEx-LGPL-2.1.txt`（随包分发）

包含文件：`winhttp.dll`（UnityDoorstop）、`doorstop_config.ini`、`.doorstop_version`、`changelog.txt`、`BepInEx/core/*`。

**LGPL-2.1 合规说明**：BepInEx 以独立 DLL 形式分发，未做静态链接、未合并、未修改，满足 LGPL-2.1 第 6 条关于「可替换 / 可重新链接」的要求。使用者可以在不重新编译本工具的前提下，用自行编译的 BepInEx 替换 `BepInEx/core/` 与 `winhttp.dll`。

### HarmonyX / MonoMod / Mono.Cecil（MIT）

| 组件 | 项目 |
|---|---|
| HarmonyX（`0Harmony`、`0Harmony20`、`HarmonyXInterop`） | <https://github.com/BepInEx/HarmonyX> |
| MonoMod（`MonoMod.Utils`、`MonoMod.RuntimeDetour`） | <https://github.com/MonoMod/MonoMod> |
| Mono.Cecil（含 Mdb / Pdb / Rocks） | <https://github.com/jbevain/cecil> |

以上组件随 BepInEx 官方发行版一同分发，版权声明见各自项目仓库。

## 三、分发边界

本仓库与发布包严格遵守以下边界：

1. **不收录、不分发、不打包任何无许可证或「保留所有权利」的第三方插件**，无论来源。
2. **不包含游戏本体文件**，也不修改游戏本体。
3. **不修改上游代码**：loader 包内所有第三方文件与官方发行版逐字节一致。
4. **LGPL 组件以独立文件分发**，不得静态链接或 ILMerge 进主程序。
5. 「第三方组件」功能只负责**管理使用者自己提供的文件**；这些文件的许可责任由提供者承担。

0.4.0 起，解锁类插件由本仓库自研的 MIT 实现（`src/BD2ModManager.SkinUnlock/`）承担：源码、构建日志、哈希都可查，不依赖任何人的闭源二进制。

## 四、许可证文本在哪里

| 文件 | 内容 |
|---|---|
| [`LICENSE`](../LICENSE) | 本仓库 MIT 全文 |
| `scripts/loader-assets/LICENSE-BepInEx-LGPL-2.1.txt` | BepInEx LGPL-2.1 全文 |
| `scripts/loader-assets/THIRD-PARTY-NOTICES.md` | 逐组件许可、版权与源码链接 |

发布包内同时带有 `LICENSE-BepInEx-LGPL-2.1.txt` 与 `THIRD-PARTY-NOTICES.md`，满足 LGPL 的附随义务。

## 五、免责声明

本工具与 Brown Dust 2 的运营方（Neowiz）无关，也未获得其授权或背书。使用 Mod 相关功能可能违反游戏服务条款，产生的一切后果由使用者自行承担。项目按「原样」提供，不对可用性、安全性或特定用途适用性作任何担保。

如果你再分发本工具或其中组件，请自行确保符合所在地法律与上游许可证要求。
