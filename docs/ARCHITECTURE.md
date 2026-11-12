# 架构与代码结构

> 面向开发者与贡献者。普通用户请先看 [README](../README.md)。

## 一、总体结构

```
┌──────────────────────────────┐
│  用户                        │
│  ├─ GUI  BD2ModManager.exe   │  Avalonia 11 / net8.0
│  └─ CLI  bd2mm.exe           │  net8.0
└──────────────┬───────────────┘
               │
┌──────────────▼───────────────┐
│  BD2ModManager.Core          │  net8.0，无 UI 依赖
│  ├─ GameLocator              │  找游戏
│  ├─ LoaderInspector / State  │  只读检查 + 状态存储
│  ├─ LoaderInstaller          │  安装 / 卸载 / 残留 / 插件开关
│  ├─ LoaderPackage            │  目录 / zip / 内嵌资源
│  └─ ComponentManager         │  第三方组件管理
└──────────────┬───────────────┘
               │ 文件操作
┌──────────────▼───────────────┐
│  游戏目录                    │
│  ├─ winhttp.dll              │  Doorstop 注入入口
│  ├─ BepInEx/core             │  loader
│  └─ BepInEx/plugins          │  插件
└──────────────────────────────┘

独立插件：BD2SkinUnlock.dll（netstandard2.0 / BepInEx 5）
          只依赖 BepInEx + HarmonyX（编译期另需 UnityEngine 引用），
          不引用主工具。
```

设计原则：**Core 只做文件与状态，GUI / CLI 只是两个外壳**。新增一个入口时不需要复制安装逻辑；新增一种包来源时只需要扩展 `LoaderPackage`。

## 二、项目布局

```
BD2ModManager/
├── README.md
├── LICENSE                      # MIT（仅覆盖本仓库自写代码）
├── Directory.Build.props        # 版本号与署名，src 下项目自动继承
├── .github/workflows/
│   ├── ci.yml                   # push / PR：编译与打包校验
│   ├── auto-tag.yml             # CI 通过后自动打 v<版本> 标签并触发发布
│   └── release.yml              # 打 v* 标签 / 手动触发：构建并发布 Release
├── docs/
│   ├── SECURITY.md
│   ├── ARCHITECTURE.md
│   ├── BUILD.md
│   ├── COMPLIANCE.md
│   └── PLUGIN_SKIN_UNLOCK.md
├── scripts/
│   ├── package_loader.py        # 从官方 release 组装 loader 包
│   ├── prepare_refs.py          # 拆出编译插件所需的引用程序集
│   ├── make_icon.py             # 生成 ico / logo（纯标准库）
│   └── loader-assets/           # LGPL 全文 + 第三方声明
├── src/
│   ├── BD2ModManager.Core/      # 跨平台核心逻辑
│   ├── BD2ModManager.Cli/       # 命令行外壳（bd2mm）
│   ├── BD2ModManager.Gui/       # 图形界面（Avalonia 11，BD2ModManager.exe）
│   └── BD2ModManager.SkinUnlock/ # BepInEx 插件本体（BD2SkinUnlock.dll，MIT）
└── tests/
    ├── BD2ModManager.Core.Tests/          # 安装 / 卸载 / 组件的离线验证
    └── BD2ModManager.SkinUnlock.Tests/    # 假游戏结构上的插件链路验证
```

`dist/`、`refs/`、`src/BD2ModManager.Gui/Assets/bd2-loader.zip`、`BD2SkinUnlock.dll` 都是构建产物，已在 `.gitignore` 中排除。二进制走 GitHub Release，不进版本库。

`GameLocator` 不能直接使用注册表的 `InstallLocation`：那里登记的是启动器而不是游戏本体，必须从该目录向下递归查找主程序。版本子目录（如 `Browndust2_10000001`）会随更新变化，不可硬编码。

## 三、Core 模块

| 文件 | 职责 | 关键点 |
|---|---|---|
| `GameLocator.cs` | 扫描游戏安装 | 从 `C:\ProgramData\Neowiz`、`%LOCALAPPDATA%\Neowiz`、Program Files、各固定/可移动磁盘根目录向下找；识别 `BrownDust II.exe`（注意带空格）与 `BrownDust II_Data`；从 `globalgamemanagers` 读取 Unity 版本；版本子目录 `Browndust2_10000001` 不硬编码 |
| `LoaderInspector.cs` | 只读检查 loader 状态 | 计算 `Present` / `CorePresent` / `HasTraces` / `ResidueOnly`；枚举 `BepInEx/plugins` 下的 `.dll` 与 `.dll.disabled`；读取 Doorstop / BepInEx 版本；不修改任何文件 |
| `LoaderInstaller.cs` | 安装、卸载、残留清理、插件开关 | 包校验、进程检查、备份、原子复制、目录记账、异常回滚、清单驱动删除；`StateStore` 也定义在此文件 |
| `LoaderPackage.cs` | 统一包来源 | 目录 / zip / 程序内嵌资源三种来源统一成目录；zip 与内嵌资源解到临时目录，交给 `LoaderInstaller`；`Dispose` 时清理临时目录 |
| `Components.cs` | 第三方组件管理 | 一个组件一个子目录；逐文件 SHA256；安装时识别 `BepInEx/plugins` 子树；排除 loader 自带插件；整包开关 / 移除 |
| `Models.cs` | 数据模型 | `GameInstall`、`LoaderStatus`、`InstallState`、`PackageManifest`、`OpResult` 等 |
| `Support.cs` | 基础工具 | `Hashing.Sha256File`、`ProcessGuard.IsGameRunning` / `RunningGameProcesses` |

### 状态存储

```
%LOCALAPPDATA%\BD2ModManager\          （Linux 为 $XDG_DATA_HOME/BD2ModManager 或 ~/.local/share/BD2ModManager）
├── state\<sha16>.json                 # loader 安装记录
├── state\<sha16>.components.json      # 第三方组件记录
└── backup\<sha16>\...                 # 被覆盖文件的原始备份
```

`<sha16>` 是游戏根目录规范化路径的 SHA256 前 16 位。状态刻意放在游戏目录之外：即使 `BepInEx` 被手工删掉，仍能读到卸载所需的信息。

`InstallState` 区分两类文件：

- `Added`：本工具新增的文件，记录 SHA256；
- `Replaced`：安装前已存在、被本工具覆盖的文件，记录原 SHA256 与备份相对路径。

卸载时只处理这两类记录；清单之外的文件一律不动。

## 四、关键流程

### 4.1 安装 loader

```
选择游戏目录
  → 解析包（目录 / zip / 内嵌资源）
  → 读取 manifest.json
  → 逐文件校验 SHA256（失败即拒绝）
  → 检查游戏进程（运行中即拒绝）
  → 检查 loader 完整性（winhttp.dll + doorstop_config.ini + BepInEx/core/BepInEx.dll）
  → 不完整时走修复安装；已有完整他人安装时无状态记录即拒绝，除非 force
  → 逐级创建目录并记录每一层
  → 已存在文件先备份，再原子复制（.bd2mm.tmp → Move）
  → 写入安装状态
  → 任一步异常：删除新增文件 + 还原备份 + 删除状态
```

### 4.2 卸载 loader

```
读取安装记录（没有记录 → 拒绝）
  → 检查游戏进程
  → 先移除所有第三方组件
  → 按记录删除 Added 文件（哈希不符则保留并报告）
  → 按备份还原 Replaced 文件
  → 清理运行时产物（cache / LogOutput.log* / loader 自带 cfg / 插件 cfg）
  → 按深度倒序删除记录过的空目录
  → 删除状态与备份
```

### 4.3 组件安装

```
GUI：用户选择目录；CLI：也可以直接传 .zip
  → Core 解压 zip 到临时目录（如有）
  → 若目录里含 BepInEx/plugins，则以其为根；否则按单个插件处理
  → 每个插件建一个 BepInEx/plugins/<名字>/ 子目录
  → 逐文件复制并记录 SHA256
  → 跳过 loader 自带的插件
  → 组件开关 = 文件改名（.dll ⇄ .dll.disabled）
```

### 4.4 插件开关

`BepInEx/plugins` 下的散装 `.dll` 由 `SetPluginEnabled` 直接改名：

- 启用：`X.dll.disabled` → `X.dll`
- 禁用：`X.dll` → `X.dll.disabled`

切换后调用方必须刷新 `DllPath`，否则下一次点击会拿旧路径判断，表现为「点一次就失效」。这是 0.4.2 修过的真实 bug。

## 五、GUI 与 CLI

### GUI

`BD2ModManager.Gui` 使用 Avalonia 11.3.22，`net8.0`，发布为 win-x64 自包含单文件。界面是 380×500 的固定小窗口，主卡片只保留：

- 顶部品牌栏：版本、最小化、关闭；
- 中央环形开关：打开时安装 loader 与内置解锁插件，关闭时移除两者并还原；
- 底部两个胶囊：按需展开「目录与环境」和「运行日志」抽屉。

第三方组件和单插件开关的 Core 能力仍保留在 `ComponentManager` / CLI 中，但不在一键解锁 GUI 里展示。

代码分工：

| 文件 | 职责 |
|---|---|
| `App.axaml` / `App.axaml.cs` | 应用入口、主题与资源 |
| `MainWindow.axaml` / `.axaml.cs` | 布局、绑定、日志滚动、确认框 |
| `MainViewModel.cs` | 状态、命令、异步操作、日志、探测与刷新 |
| `UpdateChecker.cs` | GitHub Release 版本检查，启动时请求；API 失败时用发布页重定向兜底，最后才回退缓存；读取 asset digest 供本地校验，只读元数据 |
| `AutoUpdater.cs` | 下载官方 exe 到程序所在目录（可改）；优先用 Release digest 校验，否则下载 `SHA256SUMS.txt`；显示校验阶段并替换当前程序重启 |
| `Commands.cs` | `RelayCommand`、`PluginItem`、`ComponentItem` 等绑定对象 |

GUI 通过 `MainViewModel` 调用 Core，不直接做文件操作；所有耗时操作走异步，UI 线程只负责绑定更新。

### GUI 实现约束

这些是踩坑后定下的约束，改动布局前请先读：

1. **内容区必须关横向滚动**：`HorizontalScrollBarVisibility="Disabled"`。设为 `Auto` 时测量宽度无限，卡片内 `TextWrapping` 失效，文字会把卡片撑破。
2. **内容区必须裁切**：外层 `Border ClipToBounds="True"`。`ScrollViewer` 不保证挡住溢出内容，滚不下的卡片会画到下面的日志卡片上。
3. **日志区不能抢焦点**：不要给只读 `TextBox` 设 `CaretIndex`。Avalonia 会让它进入输入态、抢走焦点，之后所有开关点击都收不到（0.2–0.4.1 的「哑巴 bug」）。
4. **日志区不用 `ScrollIntoView`**：它会冒泡，把抽屉外层也带着滚。
5. **日志高度取行高整数倍**：当前抽屉里的 `ListBox` 高 216 = 12 × 18；边框与内边距放在外层 `Border`，否则视口会出现非整数倍，滚到底时上下各露半行。
6. **滚动只滚日志列表内部的 `ScrollViewer`**：`ScrollToEnd()` 在后台优先级执行，不碰光标与选区。

### 首次启动与功能模块

首次启动或免责声明版本变化时，`App` 先显示 `DisclaimerWindow`：用户必须勾选同意才能进入主窗口，否则进程退出。勾选结果与模块开关保存在 `%LOCALAPPDATA%\BD2ModManager\modules.ini`（`ModuleSettings`），不写游戏目录。

主界面「功能模块」按钮打开 `ModulesWindow`；从关闭改为开启高风险模块（心契之约实验、全服装/全情缘/全角色本地镜像）时会再弹出 `RiskConfirmation` 二次确认。插件读取同一份 `modules.ini` 的 `[modules]` 段，每轮巡检重新加载，因此改开关不需要重启游戏；BepInEx 的 cfg 只保留低层参数。插件 1.3.0 的全服装镜像改为读取完整 `CostumeTable`（含无皮肤行/立绘/大厅动画），不再只依赖 `PrestigeSkinTable`。主程序也遵守 `Diagnostics`/`AutoUpdate`：关闭后诊断包按钮不可用、启动时不再自动检查更新，手动检查更新仍保留。

### 诊断包（Diagnostics）

`Core/Diagnostics.Export(game, store, options)` 只读采集，不打游戏文件、插件 DLL 或存档：

- `report.txt`：版本、系统、游戏与 loader 状态、插件/组件清单、配置与日志文件名、自动检查结论。
- `summary.json`：同一批数据的机器可读版本，便于 issue 模板或后续工具消费。
- `configs/`：`BepInEx/config/*.cfg` 与 `doorstop_config.ini`（单文件上限 512 KB）。
- `logs/`：`LogOutput.log*` 与 `SkinUnlock.probe.log` 的末尾（默认 200 行 / 256 KB）。

导出时把状态目录、用户目录和用户名替换为 `%APPDATA%`、`%USERPROFILE%`、`%USER%`，可直接附在公开 issue。自动检查覆盖：loader 是否完整/仅残留、是否有多个解锁插件、插件同名多份、日志末尾是否命中异常关键字、`BepInEx.cfg` 是否尚未生成。

GUI 的入口在「目录与环境」抽屉（清理残留按钮右侧）；CLI 为 `bd2mm diag [游戏目录] [--out 目录]`。

### 插件独立更新（PluginUpdate）

插件与主程序分开发版。主程序更新仍走 GitHub Release 的整包 exe；插件只更新 `BD2SkinUnlock.dll`，不重新下载 GUI。

- `Core/PluginUpdate.cs` 读取 `plugin/plugin.json`（`version/url/sha256/size/minAppVersion`），只接受 HTTPS 与 64 位 SHA256。
- `PluginUpdater.DownloadAsync` 下载到 `%LOCALAPPDATA%\BD2ModManager\plugin-update\pending-<version>\`，先写 `.download`，校验 SHA256 与大小后原子改名；最大 32 MB。
- `PluginUpdater.Apply` 在游戏运行时只保留 pending，提示退出后再装；游戏未运行时调用 `ComponentManager.ReplaceFile`，只替换安装记录里哈希仍匹配的 `BD2SkinUnlock.dll`，先备份到 `plugin-update/backup/`，失败尝试还原。
- GUI 在更新卡里显示「插件更新」行；启动时自动检查（受 `AutoUpdate` 总开关约束），也可手动检查/安装。程序重启后 `TryGetPending` 会恢复已下载状态。
- 清单与构建：`plugin/plugin.json` 是当前基线；`.github/workflows/plugin-release.yml` 在 `plugin-v*` tag 构建 Release DLL、生成清单和 `SHA256SUMS.txt` 并上传 Release。主程序发布不包含插件清单。

### CLI 命令

`BD2ModManager.Cli` 是开发调试工具，不随 Release 发布；命令分派在 `Program.cs`，实际逻辑全部调用 Core：

| 命令 | 用途 |
|---|---|
| `bd2mm find` | 扫描并列出所有 BD2 安装 |
| `bd2mm status [游戏目录]` | 查看 loader 与插件状态（只读） |
| `bd2mm install <游戏目录> --package <包目录或 zip> [--force]` | 安装 loader |
| `bd2mm uninstall <游戏目录>` | 卸载并还原 |
| `bd2mm clean-residue <游戏目录>` | 清掉 loader 残留（缓存与日志，不动插件） |
| `bd2mm diag [游戏目录] [--out 输出目录]` | 导出诊断包（默认桌面，自动脱敏） |
| `bd2mm plugin-update [游戏目录] [--check]` | 单独检查/安装插件更新（不重下主程序） |
| `bd2mm plugins <游戏目录>` | 列出插件 |
| `bd2mm enable\|disable <游戏目录> <dll>` | 单插件开关 |
| `bd2mm components <游戏目录>` | 列出第三方组件 |
| `bd2mm component-install <游戏目录> <目录或 zip>` | 安装组件 |
| `bd2mm component-remove <游戏目录> <组件名>` | 整包移除 |
| `bd2mm component-enable\|disable <游戏目录> <组件名>` | 整包开关 |

不指定游戏目录时自动探测。不带参数运行 `bd2mm.exe` 会打印帮助并等待按键，不会一闪而过。

## 六、设计取舍

- **语义是注入 / 卸载，不是替换 / 还原**。除 `winhttp.dll` 是劫持入口外，BepInEx 全部是新增文件；游戏本体零改动。
- **卸载禁止整目录删除**。只按清单删自己写过的东西；用户改过的保留。
- **区分两条分发路径**：官网 `BD2StarterSetup.exe` 安装版可支持；Google Play Games 版（`BrownDust2 (GPG)`）结构不同，应识别并拒绝。BD2 不在 Steam。
- **GUI 只保留一键开关**：没有 BepInEx 时，打开 = 安装 loader + 内置解锁插件；已有**完整** BepInEx 时，打开 = 只安装内置解锁插件，不覆盖现有 loader；如果 loader 不完整（例如只有 `winhttp.dll`、缺少 `BepInEx/core`），则视为需要修复，重新安装完整 loader 后再安装插件。
- **不做免杀**。无签名 exe 写 DLL 必被杀软误报，只能靠开源、不混淆、不上壳缓解；项目不承诺绕过安全软件。
- **源码是唯一真相**。发布物由 CI 从源码构建；仓库不提交 65 MB 的自包含 exe。

## 七、测试

两套离线测试都不需要游戏本体：

| 项目 | 覆盖 |
|---|---|
| `tests/BD2ModManager.Core.Tests` | 残留识别 / 清理、装完再卸、目录回收、组件安装 / 开关 / 移除、开关改名后旧路径失效 |
| `tests/BD2ModManager.SkinUnlock.Tests` | 反射解析、设计表读取、注入、列表补全、点击拦截、选择回放、取消 / 回滚、撤销补丁、贴图超时兜底、默认皮肤拦截、大厅场景、救赎之塔镜像；85 项检查 |

运行方式：

```bash
dotnet run --project tests/BD2ModManager.Core.Tests -c Release
dotnet run --project tests/BD2ModManager.SkinUnlock.Tests -c Release
```

CI 在 GitHub Actions 的 Linux runner 上跑真实的 Harmony 挂载；Windows 真机负责最终 GUI 验收。

## 八、扩展指引

- **加 CLI 命令**：在 `src/BD2ModManager.Cli/Program.cs` 的 `switch` 里加分派，逻辑落到 Core，不要在 CLI 里直接操作文件。
- **加 GUI 卡片**：在 `MainWindow.axaml` 加布局，在 `MainViewModel` 加绑定属性与命令；耗时操作保持 `async` + `Dispatcher` 回 UI 线程。
- **加包来源**：扩展 `LoaderPackage`，统一返回已解开的目录；`LoaderInstaller` 不需要知道来源是 zip 还是内嵌资源。
- **改插件**：先读 [PLUGIN_SKIN_UNLOCK.md](PLUGIN_SKIN_UNLOCK.md)，再改 `src/BD2ModManager.SkinUnlock/`；改完必须跑 85 项离线验证。
