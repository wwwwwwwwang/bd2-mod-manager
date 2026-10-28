# BD2ModManager

Brown Dust 2（棕色尘埃2）的 BepInEx 加载器管理器 —— 傻瓜式的安装 / 卸载 / 插件开关工具。

> 定位：**通用的 BepInEx 管理器**，不预置任何闭源插件。
> 合法性来自「只分发有再分发授权的开源组件」，内容由使用者自行提供。

## 当前状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| C0 | 第三方 mod 包的分析与合规核查 | ✅ 完成 |
| C1 | 干净的 loader 分发包（5.4.23.6） | ✅ 完成 |
| 引擎 | 探测 / 安装 / 卸载 / 插件开关 | ✅ 完成 |
| GUI | 图形界面（Avalonia，双击即用） | ✅ 完成（0.2.0） |
| 组件 | 第三方插件的安装 / 开关 / 整包移除 | ✅ 完成（0.3.0） |
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
| 现状 | 2026-10-29 已清理：早期探针（bd2-composer）留下的 BepInEx 5.4.23.5 + Doorstop 4.5.0 + Bd2Probe.dll 全部移除，`bd2mm status` 复核为「目录干净」 |

## 用法

### 图形界面（推荐）

下载 `BD2ModManager-<版本>-win-x64.exe`，**双击即可**，不需要装 .NET、不需要命令行、不需要管理员权限。

1. 打开后自动扫描游戏安装位置；没找到就点「选择目录」，选到有 `BrownDust II.exe` 的那个文件夹。
2. 打开「启用 Mod 支持」开关 → 铺开 loader；关掉开关 → 删除自己写入的文件并还原被覆盖的原文件。
3. 「第三方组件」区点「安装组件…」，选中解压后的插件目录（里面含 `.dll`，也可以直接选 `BepInEx` 的上一层）——
   工具会把每个插件装成独立子目录、记下每个文件的哈希，之后可整包开关、整包移除。
4. 散装放进去的 `.dll` 仍会出现在「插件」列表里，可单独开关。

loader 包已内嵌在 exe 里，单个文件就能用。命令行版 `bd2mm.exe` 仍然保留，给批处理 / 排查用。

> 工具**不附带**任何第三方解锁类插件（见「合规边界」）。它只负责安装与管理你自己提供的插件包，并且只删自己写过的文件。

### 命令行

```bash
bd2mm find                                         # 扫描所有 BD2 安装
bd2mm status [游戏目录]                             # 查看 loader 与插件状态（只读）
bd2mm install <游戏目录> --package <包目录或 zip> [--force] # 安装 loader
bd2mm uninstall <游戏目录>                          # 卸载并还原
bd2mm plugins <游戏目录>                            # 列出插件
bd2mm enable|disable <游戏目录> <dll>               # 插件开关

bd2mm components <游戏目录>                                  # 列出第三方组件
bd2mm component-install <游戏目录> <目录或 zip>               # 安装组件
bd2mm component-remove  <游戏目录> <组件名>                   # 整包移除
bd2mm component-enable|disable <游戏目录> <组件名>            # 整包开关
```

不指定游戏目录时自动探测。双击 `bd2mm.exe`（不带参数）会打印帮助并等待按键，不会一闪而过。

## 安全设计（这些是硬约束，不要改）

1. **先校验后写入**：包内每个文件的 sha256 与 `manifest.json` 比对，不符直接拒绝（供应链校验）。
2. **不碰别人的安装**：检测到 BepInEx 但无本工具的状态文件 → 中止，除非 `--force`。
3. **卸载清单驱动**：只删「记录过且哈希仍匹配」的文件；用户改过的保留并报告。
4. **状态存游戏目录之外**：`%LOCALAPPDATA%\BD2ModManager\`，BepInEx 目录被手工删掉也不影响卸载。
5. **逐级记录创建目录**：`CreateDirectory` 会连带建父目录，卸载时必须一起回收（已踩过坑）。
6. **装前查进程**：游戏运行时 `winhttp.dll` 写入会失败（`ERROR_SHARING_VIOLATION`）。
7. 安装中途异常自动回滚。
8. **组件同样清单驱动**：每个组件一个子目录，逐文件记哈希；移除时改过的一律保留。loader 自带的插件（如 ConfigurationManager）不归组件管，避免抢走它的文件。关闭「启用 Mod 支持」时先清组件、再删 loader，目录才能真正回到干净状态。

## 目录

```
BD2ModManager/
├── README.md
├── LICENSE                      # MIT（仅覆盖本仓库自写代码）
├── Directory.Build.props        # 版本号与署名，src 下项目自动继承
├── .gitignore / .gitattributes
├── .github/workflows/
│   ├── ci.yml                   # push / PR：编译与打包校验
│   └── release.yml              # 打 v* 标签：构建并发布 Release
├── docs/
│   ├── 开发笔记.md
│   ├── 分析存档.md
│   └── 实测记录.md
├── scripts/
│   ├── package_loader.py        # 从官方 release 组装 loader 包
│   ├── make_icon.py             # 生成 ico / logo（纯标准库，无 Pillow）
│   └── loader-assets/           # LGPL 全文 + 第三方声明
└── src/
    ├── BD2ModManager.Core/     # 跨平台核心逻辑
    ├── BD2ModManager.Cli/      # 命令行外壳（bd2mm）
    └── BD2ModManager.Gui/      # 图形界面（Avalonia 11，BD2ModManager.exe）
```

源码是唯一真相。`dist/` 是本地构建产物目录，已被 `.gitignore` 排除——二进制走 **GitHub Release**，不进版本库（自包含单文件 65 MB，提交进 git 会让仓库迅速臃肿且无法增量 diff）。

远端构建目录：`ssh.build-host.example:~/build/bd2mm/`（Linux，用于编译与逻辑验证；Avalonia 界面可在 Xvfb 里真跑并截图核对，Windows 成品在本机验收）。

## 构建与发布

### 自动（推荐）

推一个 `v*` 标签即可，GitHub Actions 会完成编译、打包、发布：

```bash
git tag v0.2.0
git push origin v0.2.0
```

流水线产出四个资产：

| 资产 | 内容 |
|---|---|
| `BD2ModManager-<版本>-win-x64.exe` | **图形界面**，自包含单文件，loader 包已内嵌（约 45 MB） |
| `bd2mm-<版本>-win-x64.exe` | 命令行版，自包含单文件 |
| `bd2-loader-<版本>.zip` | 从官方 release 现下现组的 loader 包（可单独取用） |
| `SHA256SUMS.txt` | 上述文件的校验和 |

`.github/workflows/ci.yml` 在每次 push / PR 时跑编译与打包校验，`.github/workflows/release.yml` 只在打标签时发布。

### 手动

```bash
# 组装 loader 分发包（含 SHA256 校验与清单生成）
python3 scripts/package_loader.py --out dist/bd2-loader.zip

# 把包嵌进图形界面，再发布（顺序不能反）
cp dist/bd2-loader.zip src/BD2ModManager.Gui/Assets/bd2-loader.zip
dotnet publish src/BD2ModManager.Gui/BD2ModManager.Gui.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out

# 命令行版
dotnet publish src/BD2ModManager.Cli/BD2ModManager.Cli.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

无 .NET 时先装：

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
```

### loader 包为什么由脚本生成

`scripts/package_loader.py` 不把 BepInEx 二进制存进仓库，而是**每次从官方 release 下载、按固定 SHA256 校验、再组装**。这样做有三个实际好处：

1. **可审计**：上游哈希写死在脚本里，任何篡改都会导致构建失败，而不是悄悄进入发布物。
2. **仓库干净**：52 个二进制文件不进 git，体积与 diff 噪声都省掉。
3. **确定性构建**：zip 内时间戳与排序固定，同样的输入必得同样的 SHA256（已实测两次构建完全一致：`789bb33e…`）。

许可证全文与第三方声明存放在 `scripts/loader-assets/`，随包一同分发，满足 LGPL 的附随义务。

## 合规边界

| 组件 | 许可 | 可否随工具分发 |
|---|---|---|
| BepInEx 5.4.23.6 | LGPL-2.1 | ✅ 需附许可证 + 源码链接 + 保持 DLL 可替换 |
| BepInEx.ConfigurationManager 19.1 | LGPL-3.0 | ✅ 同上 |
| HarmonyX / MonoMod / Mono.Cecil | MIT | ✅ 官方包内自带 |
| 第三方闭源插件 (`lynesth.bd2.第三方闭源插件.dll`) | 无（保留所有权利） | ❌ 不可再分发 |
| 第三方闭源插件 | 无（作者【原作者】保留，且注明「请勿倒卖」） | ❌ 不可再分发 |

工具本身建议以 **MIT** 开源发布。LGPL 组件必须以独立文件分发，不得静态链接或 ILMerge 进主程序。

**关于解锁类插件**：它既不是国外开源项目的封装，也没有任何许可证——反编译可见作者声明框（`docs/分析存档.md` §2.0）。所以本工具**不预置、不分发**它，只提供「第三方组件」能力：由使用者自己提供插件包，工具负责安装、开关、按清单移除。要真正内置一份，只能等我们自己写出等价的 MIT 实现（C2）。

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

## 署名

**燧人氏** —— 取「带来火种的人」之意，愿福泽万民。

这个工具不收费、不预置闭源插件、不抹除任何人的署名。它只做一件事：把本该属于公共领域的东西，干净地交回使用者手上。

作者：[`@wwwwwwwwang`](https://github.com/wwwwwwwwang) · 许可：[MIT](LICENSE)
