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
| C2 | 自研 MIT 解锁插件（`BD2SkinUnlock.dll`） | ✅ 完成（0.4.0） |

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
3. 「第三方组件」区点「安装内置插件」→ 装上自研的 `BD2SkinUnlock.dll`（详见「内置解锁插件」），无需另外找文件。
4. 也可以点「安装组件…」装你自己准备的插件：选中解压后的插件目录（里面含 `.dll`，也可直接选 `BepInEx` 的上一层），
   工具会把每个插件装成独立子目录、记下每个文件的哈希，之后可整包开关、整包移除。
5. 散装放进去的 `.dll` 仍会出现在「插件」列表里，可单独开关。

loader 包与解锁插件都已内嵌在 exe 里，单个文件就能用。命令行版 `bd2mm.exe` 仍然保留，给批处理 / 排查用。

> 工具自带的解锁插件是本仓库自己写的 MIT 实现，不含任何第三方闭源代码。第三方组件区只负责安装与管理你自己提供的包，
> 并且只删自己写过的文件、改过的一律保留。

### 命令行

```bash
bd2mm find                                         # 扫描所有 BD2 安装
bd2mm status [游戏目录]                             # 查看 loader 与插件状态（只读）
bd2mm install <游戏目录> --package <包目录或 zip> [--force] # 安装 loader
bd2mm uninstall <游戏目录>                          # 卸载并还原
bd2mm clean-residue <游戏目录>                      # 清掉 loader 残留（缓存与日志，不动插件）
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
9. **卸载连运行时产物一起清**：`cache`、`LogOutput.log`、loader 自带的两份 cfg 是 loader 启动时自己生成的，不在安装清单里，卸载要单独处理，否则目录会留个空壳。
10. **横向滚动必须关掉**：主 `ScrollViewer` 留 `HorizontalScrollBarVisibility="Auto"` 时会用无限宽测量内容，卡片里的 `TextWrapping` 全部失效、长句子把卡片撑破。这个坑只在「内容刚好比窗口宽」时出现，不报错。
11. **日志区有三条禁忌**：① 不能设 `CaretIndex`——会抢焦点，之后窗口里所有开关的点击都被吞掉（0.2~0.4.1 的哑巴 bug）；② 不能把 TextBlock/TextBox 塞进固定高度容器再指望 `ScrollToEnd`——它们报的内容高度不可靠；③ `ScrollIntoView` 会冒泡，把整页 ScrollViewer 带着滚。现在固定底部 + ListBox + 关虚拟化 + 只滚列表内部。
12. **残留不等于他人安装**：`winhttp.dll` 与 `BepInEx/core/BepInEx.dll` 都不在时，游戏根本加载不了插件，此时判为「仅剩残留」并允许一键清理；核心仍在就拒绝，必须走正常还原。
13. **「残留」只算本工具能清的东西**：缓存、日志、loader 自带配置。`plugins` 里的插件是用户的，删不掉也不该删——不能因为它在就把状态卡在「清不掉」。

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
│   ├── prepare_refs.py          # 从 loader 包拆出编译插件要用的引用程序集
│   ├── make_icon.py             # 生成 ico / logo（纯标准库，无 Pillow）
│   └── loader-assets/           # LGPL 全文 + 第三方声明
├── src/
│   ├── BD2ModManager.Core/      # 跨平台核心逻辑
│   ├── BD2ModManager.Cli/       # 命令行外壳（bd2mm）
│   ├── BD2ModManager.Gui/       # 图形界面（Avalonia 11，BD2ModManager.exe）
│   └── BD2ModManager.SkinUnlock/# BepInEx 插件本体（BD2SkinUnlock.dll，MIT）
└── tests/
    └── BD2ModManager.SkinUnlock.Tests/  # 假游戏结构的离线验证（不需要游戏本体）
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
| `BD2SkinUnlock.dll` | 自研解锁插件，已内嵌在图形界面里；单文件版给手动安装或旧版工具用 |
| `SHA256SUMS.txt` | 上述文件的校验和 |

`.github/workflows/ci.yml` 在每次 push / PR 时跑编译与打包校验，`.github/workflows/release.yml` 只在打标签时发布。

### 手动

```bash
# 1. 组装 loader 分发包（含 SHA256 校验与清单生成）
python3 scripts/package_loader.py --out dist/bd2-loader.zip

# 2. 从 loader 包里拆出编译插件要用的 BepInEx / Harmony 引用
python3 scripts/prepare_refs.py --zip dist/bd2-loader.zip

# 3. 编译解锁插件，并跑一遍离线验证
dotnet build src/BD2ModManager.SkinUnlock/BD2ModManager.SkinUnlock.csproj -c Release
dotnet run --project tests/BD2ModManager.SkinUnlock.Tests -c Release

# 4. 把 loader 包与插件嵌进图形界面，再发布（顺序不能反）
cp dist/bd2-loader.zip src/BD2ModManager.Gui/Assets/bd2-loader.zip
cp src/BD2ModManager.SkinUnlock/bin/Release/netstandard2.0/BD2SkinUnlock.dll \
   src/BD2ModManager.Gui/Assets/BD2SkinUnlock.dll
dotnet publish src/BD2ModManager.Gui/BD2ModManager.Gui.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out

# 命令行版
dotnet publish src/BD2ModManager.Cli/BD2ModManager.Cli.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

插件只引用「引用程序集」，编译不需要游戏本体；`UnityEngine.Modules 2021.3.33` 从 NuGet 取（`ExcludeAssets="runtime"`，
只用于编译，不会进产物），BepInEx 与 HarmonyX 则必须与运行时同版本，所以从我们自己组的 loader 包里拆。

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

**关于解锁类插件**：某平台那份既不是国外开源项目的封装，也没有任何许可证——反编译可见作者声明框（`docs/分析存档.md` §2.0）。
所以本工具**不分发**它，只用「第三方组件」能力管理使用者自己提供的包。

0.4.0 起这个位置由本仓库自研的 MIT 实现顶上了（`src/BD2ModManager.SkinUnlock/`，见下一节），
源码、构建日志、哈希都可查，不再依赖任何人的闭源二进制。

## 内置解锁插件

`src/BD2ModManager.SkinUnlock/` 是一份独立的 BepInEx 5 插件（MIT），产物 `BD2SkinUnlock.dll`。
它只做两件事：

1. **让已拥有的服装能选到它全部皮肤** —— 皮肤清单来自游戏自带的设计表（`PrestigeSkinTable`），不硬编码任何 id；
2. **点击皮肤时只改本地内存**，不给服务器发切换请求。

**不做什么**：不会凭空造出你没买的服装（那部分数据由服务端下发）；不发任何网络请求；不碰游戏本体文件。

### 和某平台那份的差别

| | 第三方闭源插件 | BD2SkinUnlock |
|---|---|---|
| 许可 | 无（作者保留，注明「请勿倒卖」） | MIT |
| 编译期依赖 | 直接引用游戏程序集 | 全反射，游戏改结构只会让功能失效，不会让程序集加载失败 |
| 找字典 | 反复遍历所有程序集的所有静态字段 | 解析一次并缓存 |
| 周期性清理 | 每 3 秒全表扫一遍 | 只校验自己补过的条目，代价 O(补入条数) |
| 出错处理 | 吞掉异常、跳过原方法 | 交还原方法，游戏照常走 |
| 退出时 | 不还原 | 移除注入条目 + 撤销补丁 |
| 同类插件并存 | 两套补丁互相覆盖 | 检测到就自动停用自己并说明 |
| Harmony 版本 | 绑定编译期那一版 | 反射调用 `Patch`，按运行时实际签名 |

### 配置

`BepInEx/config/com.suiren.bd2.skinunlock.cfg`

| 项 | 默认 | 说明 |
|---|---|---|
| `General.Enabled` | true | 总开关，关掉后不介入游戏（需重启生效） |
| `General.ReapplyIntervalSeconds` | 2 | 把已选皮肤写回服装的间隔。服务器同步会覆盖本地选择，靠这个恢复 |
| `General.MigrateLegacyConfig` | true | 首次运行导入旧插件 `第三方闭源插件.cfg` 里的选择，导完把原文件改名 |
| `Diagnostics.Verbose` | false | 排查时打开，输出每轮巡检细节 |

选择记录单独存在 `BepInEx/config/com.suiren.bd2.skinunlock.selection.txt`，`costumeId=designId` 每行一条，
和旧格式一致，两边可以互导。

### 验证到哪一步

`tests/BD2ModManager.SkinUnlock.Tests` 用一套假游戏结构（`Proto.Net.CostumeDBInfo` / `Proto.Net.PrestigeSkinDBInfo` /
`Proto.Design.common.PrestigeSkinTable` / `RawDataManager`，字段布局按真实客户端摆）把整条链路跑一遍：

```
dotnet run --project tests/BD2ModManager.SkinUnlock.Tests -c Release
```

覆盖反射解析、读设计表、注入条目、补全列表、拦截点击、单一选中、服务器覆盖后回放、取消、回滚、撤销补丁，
**32 项检查全部通过**。CI 跑在 GitHub Actions 上（`/tmp` 可执行），两处补丁是**真实挂载**到方法上跑完整条链路的：

```
log  补丁已挂上：查询服装皮肤 → CostumeDBInfo.GetConnectedPrestigeSkinDBInfos
log  补丁已挂上：切换皮肤 → Database.SetSkin
[OK] 两个补丁都挂到了真实方法上
全部通过：32 项检查。
```

没有覆盖的是真实游戏里的字段名与时机——这一层只能进游戏看日志：
插件启动时会按步骤把进度写进 `BepInEx\LogOutput.log`，解析不到会说清卡在哪一步（`Step`）和原因（`Problem`）。

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
