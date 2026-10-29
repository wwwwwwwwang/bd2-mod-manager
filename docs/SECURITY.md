# 安全设计与硬约束

> 面向关心「这个管理器会不会夹带私货、乱删文件」的用户与审计者。
> 本文描述当前实现必须遵守的约束；源码是唯一真相。

## 一、安全目标

BD2ModManager 只做两件事：

1. 把一份**可审计的 BepInEx loader** 铺到游戏目录，并能完整收回；
2. 管理**使用者自己提供**的第三方插件，以及内置的 MIT 皮肤解锁插件。

它刻意不做这些事：

- **不修改游戏本体**：`BrownDust II.exe`、`BrownDust II_Data` 与存档都不碰。
- **不联网**：GUI / CLI / 内置插件运行时都不发网络请求；loader 包只在构建阶段从官方 release 下载。
- **不删除来源不明的文件**：没有安装记录就不做通用卸载；残留清理也只碰已知的 loader 运行时产物。
- **不收录闭源插件**：发布包只含本仓库代码与有再分发许可的开源组件。

信任边界由三部分组成：**BepInEx 官方 release**、**本仓库源码**、**使用者自己提供的插件**。

需要特别说明：**第三方插件本身不在本工具的安全承诺内**。管理器只做文件级安装 / 开关 / 移除，不审计 DLL 的行为；插件能做什么由插件作者决定。不要安装来源不明的插件。

## 二、威胁模型

| 风险 | 防线 | 约束 |
|---|---|---|
| 下载到的 loader 包被替换或篡改 | 上游发行包固定 SHA256；包内逐文件校验 `manifest.json` | 1 |
| 覆盖别人手工装的 BepInEx，破坏其环境 | 无本工具状态文件时中止，必须显式 `--force` | 2 |
| 卸载时误删用户改过的文件 | 只删「记录过且哈希仍匹配」的文件 | 3 |
| 状态目录随游戏目录被一起删掉 | 安装状态存放在游戏目录之外 | 4 |
| 卸载后留下空目录 / 空壳 | 逐级记录创建的目录，按深度倒序回收 | 5、9 |
| 游戏运行时写 `winhttp.dll` 失败 | 安装前检查游戏进程 | 6 |
| 安装中途异常留下半套文件 | 自动回滚新增文件并还原备份 | 7 |
| 第三方组件被误删或抢走 loader 自带插件 | 组件独立子目录、逐文件哈希、排除 loader 自带项 | 8 |
| 把残留误判成「他人安装」而无法清理 | 用核心文件与可清理痕迹判断，而不是看目录是否存在 | 10、11 |

## 三、11 条安装 / 卸载硬约束

### 1. 先校验后写入

包内每个文件的 SHA256 必须与 `manifest.json` 一致，不符直接拒绝。校验发生在任何写入之前，属于供应链校验，不是「装完再检查」。

### 2. 不碰别人的安装

检测到 BepInEx，但游戏目录里没有本工具的安装状态 → **中止安装**。只有显式传入 `--force`（或确认强制模式）才允许继续。卸载同理：没有安装记录就拒绝删除，并提示手工清理路径。

### 3. 卸载清单驱动

卸载只处理安装时记录过的文件：

- 本工具新增的文件：哈希仍匹配才删除；
- 被覆盖的原文件：从备份还原；
- 用户改过的文件：**保留并报告**，绝不按文件名盲删。

禁止 `rm -rf BepInEx` 这类整目录删除。

### 4. 状态存在游戏目录之外

安装记录、组件记录与备份放在：

```
%LOCALAPPDATA%\BD2ModManager\
├── state\<hash>.json
├── state\<hash>.components.json
└── backup\<hash>\...
```

即使 `BepInEx` 目录被手工删掉，卸载仍能读到「当初装了什么、覆盖了什么」。

### 5. 逐级记录创建的目录

`Directory.CreateDirectory(叶子目录)` 会连带创建所有父目录。安装时必须逐级 `EnsureDir()`，把**每一层真正新建的目录**都记下来；卸载时按路径深度倒序删除，否则会留下 `BepInEx\`、`BepInEx\plugins\` 这类空壳。

### 6. 装前检查游戏进程

游戏运行时 `winhttp.dll` 被占用，写入会失败（`ERROR_SHARING_VIOLATION`）。安装、卸载、组件操作、切换插件、清理残留前都先检查 `BrownDust II` / `BrownDust2` / `Browndust2` 进程；检测到就拒绝操作并提示先完全退出游戏。

### 7. 安装中途异常自动回滚

安装过程中出现异常时：

1. 删除本次新增的文件；
2. 把本次覆盖过的文件从备份还原；
3. 删除本次写入的状态文件。

目标是「要么装好，要么回到安装前」，不留半套。

### 8. 第三方组件同样清单驱动

- 一个组件 = `BepInEx/plugins/<名字>/` 一个独立子目录（单文件插件也建目录）；
- 每个文件记录 SHA256，移除时逐项校验，改过的一律保留；
- loader 自带的插件（如 `ConfigurationManager`）不归组件管，避免抢走 loader 的文件；
- 关闭「启用 Mod 支持」时**先清组件、再删 loader**，目录才能真正回到干净状态。

### 9. 卸载连运行时产物一起清

`cache`、`LogOutput.log*`、loader 自带的两份 cfg、内置插件的 cfg / 探针日志，都是 loader 或插件第一次启动时自己生成的，不在安装清单里。卸载必须单独处理它们，否则 `BepInEx` 目录会留个空壳，下次检测还会被误判成「他人安装」。

玩家的皮肤选择记录（`com.suiren.bd2.skinunlock.selection.txt`）只在**卸载 loader** 时清；单纯「清理残留」会保留，避免清一次缓存把选好的皮肤全丢掉。

### 10. 残留不等于他人安装

`winhttp.dll` 与 `BepInEx/core/BepInEx.dll` 都不在时，游戏根本加载不了任何插件。此时判为「仅剩残留」，允许一键清理；只要核心仍在，就拒绝残留清理，必须走正常还原流程，免得把一份能用的 loader 拆坏。

### 11. 「残留」只算本工具能清的东西

缓存、日志、loader 自带配置、内置插件诊断日志，以及残缺的 `winhttp.dll` / Doorstop / core 才算痕迹。`plugins` 里的插件是用户的，删不掉也不该删——不能因为它在，就把状态永久卡在「清不掉」。

判定使用 `HasTraces` 与 `ResidueOnly`，而不是「`BepInEx` 目录是否存在」：

```csharp
ResidueOnly = Present && !CorePresent && HasTraces;
```

## 四、附录：2 条界面稳定性约束

这两条不涉及安全，但同样是踩过坑后定下的硬约束，完整背景见 [ARCHITECTURE.md](ARCHITECTURE.md#gui-实现约束)。

### 12. 内容区横向滚动必须关掉，并套裁切层

- `HorizontalScrollBarVisibility="Auto"` 会用无限宽测量内容，卡片里的 `TextWrapping` 全部失效，长句子把卡片撑破；
- 滚动容器不保证挡住溢出内容，外层必须加 `ClipToBounds="True"`，否则滚不下的卡片会画到下面固定住的日志卡片上。

### 13. 日志区不能抢焦点，高度必须是行高的整数倍

- 不给只读 `TextBox` 设 `CaretIndex`：会抢焦点，之后窗口里所有开关的点击都被吞掉；
- 不用「固定高度容器 + `ScrollToEnd`」：内容高度不可靠；
- 不让 `ScrollIntoView` 冒泡：会把整页 `ScrollViewer` 带着滚；
- `ListBox` 的边框与内边距移到外层，列表高度取行高整数倍（当前 5 × 18），避免滚到底时上下各露半行。

## 五、本工具会写哪些文件

| 位置 | 内容 | 何时产生 |
|---|---|---|
| 游戏目录根 | `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`changelog.txt`、`VERSION.txt` | 安装 loader |
| `BepInEx/**` | 官方 loader 文件、`ConfigurationManager`、翻译、许可证 | 安装 loader |
| `BepInEx/plugins/<组件名>/**` | 使用者自己提供的插件 | 安装组件 |
| `BepInEx/cache`、`LogOutput.log*`、`BepInEx/config/*.cfg` | loader / 插件运行时生成 | 启动游戏后 |
| `BepInEx/SkinUnlock.probe.log` | 内置插件诊断日志 | 启动游戏后 |
| `%LOCALAPPDATA%\BD2ModManager\` | 安装记录、组件记录、被覆盖文件的备份 | 安装 / 组件操作 |

除上述位置与包解压用的临时目录外，本工具不写任何文件。

## 六、如何自行核验

1. **核对发布物**：Release 附带 `SHA256SUMS.txt`，下载后逐个比对。
2. **核对 loader 包**：解开 `bd2-loader.zip`，用 `manifest.json` 里的 `path + size + sha256` 校验每个文件。
3. **核对上游**：`scripts/package_loader.py` 里写死了 BepInEx 5.4.23.6 与 ConfigurationManager 19.1 官方发行包的 SHA256；构建时会重新下载并校验。
4. **从源码构建**：步骤见 [BUILD.md](BUILD.md)。仓库不提交二进制，避免「源码与发布物对不上」。

Windows 下快速计算哈希：

```powershell
Get-FileHash .\BD2ModManager-<版本>-win-x64.exe -Algorithm SHA256
```

## 七、报告问题

发现安全问题时，请通过仓库 Issues 联系维护者，并尽量附上复现步骤与版本号；不要在公开 Issue 里粘贴可直接利用的细节。项目地址：<https://github.com/wwwwwwwwang/bd2-mod-manager>。
