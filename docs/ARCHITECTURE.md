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
│   └── release.yml              # 打 v* 标签：构建并发布 Release
├── docs/
│   ├── SECURITY.md
│   ├── ARCHITECTURE.md
│   ├── BUILD.md
│   ├── COMPLIANCE.md
│   ├── PLUGIN_SKIN_UNLOCK.md
│   └── images/gui-main.png
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
  → 检查是否已有他人安装（无状态记录即拒绝，除非 force）
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
  → 跳过 loader 自带的 ConfigurationManager
  → 组件开关 = 文件改名（.dll ⇄ .dll.disabled）
```

### 4.4 插件开关

`BepInEx/plugins` 下的散装 `.dll` 由 `SetPluginEnabled` 直接改名：

- 启用：`X.dll.disabled` → `X.dll`
- 禁用：`X.dll` → `X.dll.disabled`

切换后调用方必须刷新 `DllPath`，否则下一次点击会拿旧路径判断，表现为「点一次就失效」。这是 0.4.2 修过的真实 bug。

## 五、GUI 与 CLI

### GUI

`BD2ModManager.Gui` 使用 Avalonia 11.3.22，`net8.0`，发布为 win-x64 自包含单文件。界面是单窗口、卡片式布局：

- 游戏位置卡片：路径、重新检测、选择目录、状态与「清理残留」；
- 「启用 Mod 支持」卡片：loader 总开关；
- 第三方组件卡片：安装内置插件 / 安装组件… / 整包开关 / 移除；
- 插件卡片：散装 `.dll` 列表与单插件开关；
- 底部固定日志卡片：最多 500 行，自动滚到最新一行。

代码分工：

| 文件 | 职责 |
|---|---|
| `App.axaml` / `App.axaml.cs` | 应用入口、主题与资源 |
| `MainWindow.axaml` / `.axaml.cs` | 布局、绑定、日志滚动、确认框 |
| `MainViewModel.cs` | 状态、命令、异步操作、日志、探测与刷新 |
| `Commands.cs` | `RelayCommand`、`PluginItem`、`ComponentItem` 等绑定对象 |

GUI 通过 `MainViewModel` 调用 Core，不直接做文件操作；所有耗时操作走异步，UI 线程只负责绑定更新。

### GUI 实现约束

这些是踩坑后定下的约束，改动布局前请先读：

1. **内容区必须关横向滚动**：`HorizontalScrollBarVisibility="Disabled"`。设为 `Auto` 时测量宽度无限，卡片内 `TextWrapping` 失效，文字会把卡片撑破。
2. **内容区必须裁切**：外层 `Border ClipToBounds="True"`。`ScrollViewer` 不保证挡住溢出内容，滚不下的卡片会画到下面的日志卡片上。
3. **日志区不能抢焦点**：不要给只读 `TextBox` 设 `CaretIndex`。Avalonia 会让它进入输入态、抢走焦点，之后所有开关点击都收不到（0.2–0.4.1 的「哑巴 bug」）。
4. **日志区不用 `ScrollIntoView`**：它会冒泡，把整页 `ScrollViewer` 带着滚。
5. **日志高度取行高整数倍**：当前 `ListBox` 高 90 = 5 × 18；边框与内边距放在外层 `Border`，否则视口会出现 101.33 这类非整数倍，滚到底时上下各露半行。
6. **滚动只滚日志列表内部的 `ScrollViewer`**：`ScrollToEnd()` 在后台优先级执行，不碰光标与选区。

### CLI 命令

`BD2ModManager.Cli` 输出 `bd2mm.exe`，命令分派在 `Program.cs`，实际逻辑全部调用 Core：

| 命令 | 用途 |
|---|---|
| `bd2mm find` | 扫描并列出所有 BD2 安装 |
| `bd2mm status [游戏目录]` | 查看 loader 与插件状态（只读） |
| `bd2mm install <游戏目录> --package <包目录或 zip> [--force]` | 安装 loader |
| `bd2mm uninstall <游戏目录>` | 卸载并还原 |
| `bd2mm clean-residue <游戏目录>` | 清掉 loader 残留（缓存与日志，不动插件） |
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
- **开关两层**：loader 总开关（铺 / 收文件）+ 单插件开关（`.dll` ⇄ `.dll.disabled`）。
- **不做免杀**。无签名 exe 写 DLL 必被杀软误报，只能靠开源、不混淆、不上壳缓解；项目不承诺绕过安全软件。
- **源码是唯一真相**。发布物由 CI 从源码构建；仓库不提交 65 MB 的自包含 exe。

## 七、测试

两套离线测试都不需要游戏本体：

| 项目 | 覆盖 |
|---|---|
| `tests/BD2ModManager.Core.Tests` | 残留识别 / 清理、装完再卸、目录回收、组件安装 / 开关 / 移除、开关改名后旧路径失效 |
| `tests/BD2ModManager.SkinUnlock.Tests` | 反射解析、设计表读取、注入、列表补全、点击拦截、选择回放、取消 / 回滚、撤销补丁、贴图超时兜底、默认皮肤拦截、大厅场景；62 项检查 |

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
- **改插件**：先读 [PLUGIN_SKIN_UNLOCK.md](PLUGIN_SKIN_UNLOCK.md)，再改 `src/BD2ModManager.SkinUnlock/`；改完必须跑 62 项离线验证。
