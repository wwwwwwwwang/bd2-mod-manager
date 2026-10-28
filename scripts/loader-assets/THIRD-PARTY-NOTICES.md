# 第三方组件声明 / Third-Party Notices

本包仅包含以下开源组件，均为官方发行版原始文件，未经修改（逐字节 SHA256 校验）。

## 1. BepInEx 5.4.23.6 (x64)

- 项目：https://github.com/BepInEx/BepInEx
- 许可：**LGPL-2.1**（全文见 `LICENSE-BepInEx-LGPL-2.1.txt`）
- 版权：Copyright (c) BepInEx contributors
- 包含文件：`winhttp.dll`（UnityDoorstop）、`doorstop_config.ini`、`.doorstop_version`、`changelog.txt`、`BepInEx/core/*`
- 源码获取：https://github.com/BepInEx/BepInEx/tree/v5.4.23.6
- 发行版来源：https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6
- 发行包 SHA256：`6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`

**LGPL-2.1 合规说明**：本包以独立 DLL 形式分发 BepInEx，未做静态链接、未合并、未修改，
因此满足 LGPL-2.1 第 6 条关于「可替换 / 可重新链接」的要求。你可以在不重新编译本工具的前提下，
用自行编译的 BepInEx 替换 `BepInEx/core/` 与 `winhttp.dll`。

## 2. BepInEx.ConfigurationManager 19.1

- 项目：https://github.com/BepInEx/BepInEx.ConfigurationManager
- 许可：**LGPL-3.0**（全文见 `BepInEx/plugins/ConfigurationManager/LICENSE`）
- 版权：Copyright (c) BepInEx contributors
- 包含文件：`BepInEx/plugins/ConfigurationManager/*`、`BepInEx/Translation/*`
- 源码获取：https://github.com/BepInEx/BepInEx.ConfigurationManager/tree/v19.1
- 发行版来源：https://github.com/BepInEx/BepInEx.ConfigurationManager/releases/tag/v19.1
- 发行包 SHA256：`dc10b1e7435dd3e0d32658b3f0f4230ddd71750164963b1095deca3bc3a98275`

**LGPL-3.0 合规说明**：同上，独立 DLL 分发、未修改。本包提供完整源码链接，
并允许使用者将该 DLL 替换为自行编译的版本（LGPL-3.0 第 4 条 d 项）。

## 3. BepInEx 的运行时依赖（随官方发行版一同分发）

| 组件 | 许可 | 项目 |
|---|---|---|
| HarmonyX (0Harmony, 0Harmony20, HarmonyXInterop) | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod (MonoMod.Utils, MonoMod.RuntimeDetour) | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil（含 Mdb / Pdb / Rocks） | MIT | https://github.com/jbevain/cecil |

以上组件的版权声明见各自项目仓库。

## 本包不包含

- 任何闭源插件（不具备再分发授权的第三方 DLL 一律未包含）
- 任何游戏本体文件
- 任何修改过的上游代码

如果发行方在本包之外分发其他插件，那些插件的许可责任由发行方自行承担。
