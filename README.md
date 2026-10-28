# BD2ModManager

Brown Dust 2（棕色尘埃2）的 BepInEx 加载器管理器 —— 傻瓜式的安装 / 卸载 / 插件开关工具。

> 定位：**通用的 BepInEx 管理器**，不预置任何闭源插件。
> 合法性来自「只分发有再分发授权的开源组件」，内容由使用者自行提供。

## 当前状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| C0 | 第三方 mod 包的分析与合规核查 | ✅ 完成 |
| C1 | 干净的 loader 分发包（5.4.23.6） | ✅ 完成 |
| 引擎 | 探测 / 安装 / 卸载 / 插件开关（CLI 已验证） | ✅ 完成 |
| GUI | 图形界面 | ⬜ 待做 |
| C2 | 自研 MIT 开源插件 | ⬜ 待定 |

## 实测环境（本机真机）

| 项 | 值 |
|---|---|
| 游戏目录 | `E:\Neowiz\Browndust2\Browndust2_10000001` |
| 主程序 | `BrownDust II.exe`（**注意带空格，不是 BrownDust2.exe**） |
| 数据目录 | `BrownDust II_Data` |
| Unity | 2022.3.62f2 |
| 启动器 | `C:\ProgramData\Neowiz\Browndust2Starter\Browndust2Starter.exe` |
| 写权限 | 目录可写，**不需要管理员权限** |
| 版本子目录 | `Browndust2_10000001` 会随更新变化，不可硬编码 |
| 现状 | 已存在他人安装：BepInEx 5.4.23.5 + Doorstop 4.5.0 + Bd2Probe.dll |

## 用法

```bash
bd2mm find                                         # 扫描所有 BD2 安装
bd2mm status [游戏目录]                             # 查看 loader 与插件状态（只读）
bd2mm install <游戏目录> --package <包目录> [--force] # 安装 loader
bd2mm uninstall <游戏目录>                          # 卸载并还原
bd2mm plugins <游戏目录>                            # 列出插件
bd2mm enable|disable <游戏目录> <dll>               # 插件开关
```

不指定游戏目录时自动探测。

## 安全设计（这些是硬约束，不要改）

1. **先校验后写入**：包内每个文件的 sha256 与 `manifest.json` 比对，不符直接拒绝（供应链校验）。
2. **不碰别人的安装**：检测到 BepInEx 但无本工具的状态文件 → 中止，除非 `--force`。
3. **卸载清单驱动**：只删「记录过且哈希仍匹配」的文件；用户改过的保留并报告。
4. **状态存游戏目录之外**：`%LOCALAPPDATA%\BD2ModManager\`，BepInEx 目录被手工删掉也不影响卸载。
5. **逐级记录创建目录**：`CreateDirectory` 会连带建父目录，卸载时必须一起回收（已踩过坑）。
6. **装前查进程**：游戏运行时 `winhttp.dll` 写入会失败（`ERROR_SHARING_VIOLATION`）。
7. 安装中途异常自动回滚。

## 目录

```
BD2ModManager/
├── README.md
├── LICENSE                      # MIT（仅覆盖本仓库自写代码）
├── .gitignore
├── docs/
│   ├── 开发笔记.md
│   ├── 分析存档.md
│   └── 实测记录.md
└── src/
    ├── BD2ModManager.Core/     # 跨平台核心逻辑
    └── BD2ModManager.Cli/      # 命令行外壳（bd2mm）
```

源码是唯一真相。`dist/` 是本地构建产物目录，已被 `.gitignore` 排除——二进制走 **GitHub Release**，不进版本库（自包含单文件 65 MB，提交进 git 会让仓库迅速臃肿且无法增量 diff）。

远端构建目录：`ssh.build-host.example:~/build/bd2mm/`（Linux，仅用于编译与逻辑验证；WPF 等 Windows 专属 UI 无法在该环境运行）。

## 构建

服务器（Linux，无 .NET 时先装）：

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

dotnet build src/BD2ModManager.Cli/BD2ModManager.Cli.csproj -c Release
dotnet publish src/BD2ModManager.Cli/BD2ModManager.Cli.csproj -c Release \
  -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

## 合规边界

| 组件 | 许可 | 可否随工具分发 |
|---|---|---|
| BepInEx 5.4.23.6 | LGPL-2.1 | ✅ 需附许可证 + 源码链接 + 保持 DLL 可替换 |
| BepInEx.ConfigurationManager 19.1 | LGPL-3.0 | ✅ 同上 |
| HarmonyX / MonoMod / Mono.Cecil | MIT | ✅ 官方包内自带 |
| 第三方闭源插件 (`lynesth.bd2.第三方闭源插件.dll`) | 无（保留所有权利） | ❌ 不可再分发 |
| 第三方闭源插件 | 无（作者保留） | ❌ 不可再分发 |

工具本身建议以 **MIT** 开源发布。LGPL 组件必须以独立文件分发，不得静态链接或 ILMerge 进主程序。

## C1 包含什么

```
winhttp.dll                          UnityDoorstop 4.6.0（官方原件）
doorstop_config.ini                  官方原件（4.x 语法：target_assembly）
.doorstop_version                    官方原件
changelog.txt                        官方原件
VERSION.txt                          版本标记
BepInEx/core/*                       官方 18 个文件
BepInEx/plugins/ConfigurationManager 官方 19.1，自带 LICENSE
BepInEx/Translation/*                CM 的 27 语翻译
LICENSE-BepInEx-LGPL-2.1.txt         LGPL-2.1 全文
THIRD-PARTY-NOTICES.md               逐组件许可与源码链接
manifest.json                        54 文件 path+size+sha256
```

## 设计要点

- **语义是注入/卸载，不是替换/还原**。除 `winhttp.dll` 是劫持外，BepInEx 全部是新增文件，游戏本体零改动。
- **卸载禁止 `rm -rf BepInEx`**。
- **区分两条分发路径**：官网 `BD2StarterSetup.exe` 安装版可支持；Google Play Games 版（`BrownDust2 (GPG)`）结构不同，应识别并拒绝。BD2 **不在 Steam**。
- **开关两层**：loader 总开关（铺/收文件）+ 单插件开关（`.dll` ↔ `.dll.disabled`）。
- 无签名 exe 写 DLL 必被杀软误报，只能靠开源/不混淆/不上壳缓解，**不做免杀**。
