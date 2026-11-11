# 内置皮肤解锁插件

`BD2SkinUnlock.dll` 是本仓库自研的 BepInEx 5 插件（MIT），已内嵌在 GUI 中，不单独发布。

一句话：**不改变游戏数值、不联网，仅让已拥有服装的皮肤与可选的大厅互动在本地显示出来。**

## 一、它做什么

只对**你已经拥有的服装**生效：

1. **补全皮肤列表**：从游戏自带的 `PrestigeSkinTable` 设计表读出「这件服装有哪些皮肤」，把未购买的皮肤条目补进本地列表；不硬编码任何 id，游戏更新后新增的皮肤会自动出现。
2. **点击只改本地内存**：拦下 `Database.SetSkin`，不向服务器发送切换请求；选择记录持久化在本地文件，游戏退出时只移除注入条目并撤销补丁；关闭总开关（重启后）插件不再介入。
3. **服务器覆盖后回放**：服务器同步会把本地选择冲掉，插件按固定间隔（默认 3 秒）把已选皮肤写回服装。
4. **贴图回调兜底**：皮肤弹窗取头像时，`LoadTexture` 0.5 秒没回调就补 `null`，未拥有的皮肤不会一直转圈。
5. **拦下无效默认皮肤**：`designId=0` 或设计表里查不到的条目不再写进皮肤字典。
6. **补全大厅互动场景**：把 `LobbySettingItemTable` 里 `PrestigeSkinId != 0` 的道具补进道具字典（Type=25），并记账以便退出时还原。
7. **退出时回滚**：移除自己补入的条目，把清理脏条目时移走的游戏原始条目放回去；`OnApplicationQuit` 撤销补丁。
8. **补全大厅互动入口**：可把 `LobbySettingItemTable` 里 `InteractionId > 0` 的全部互动道具补进道具字典（Type=25），并让「心契之约」列表拿到全部 `Type=2` 互动；默认开启，可用 `Interactions.UnlockAllInteractions` 关闭。
9. **放行互动解锁判定**：拦截 `CheckInteractionUnlock` 并返回 `true`，默认开启，可用 `Interactions.BypassInteractionUnlock` 关闭。
10. **关闭内容审查**：拦截 `CensorshipManager.IsCensorship` 并返回 `false`，用于按地区隐藏立绘/互动的客户端；默认开启，可用 `Interactions.DisableCensorship` 关闭。
11. **大厅立绘映射**：把大厅立绘槽位指向当前选择的皮肤；也可以用 `Skins.LobbyDesignId` 强制指定 designId。默认开启，可用 `Skins.LobbyPortrait` 关闭。
12. **心契之约实验开关**：`Interactions.UnlockCovenantList`（默认关）会替换 `SpineInteractionUI.SetUI` 的互动列表，并放行互动界面的 `Can/Interact/Unlock/Possible` 布尔门与约会服装谓词。不同游戏版本可能界面异常，建议先用默认配置。
13. **恢复互动界面**：同一实验开关下，给 `SpineInteractionUI` 的 `(int,int)` 刷新方法挂 postfix，把原版关掉的互动工具栏、缩放条重新打开，并换一句操作提示；只操作 UI 对象，不写存档。
14. **全服装本地镜像（高风险，默认关）**：把设计表里所有带皮肤的服装补进本地服装字典（`InvenIndex=800000000+`，套第一套皮肤设计），让未拥有的服装在本地列表出现；关闭开关会整批移除。只影响客户端显示，服务器仍认为未拥有，可能造成界面/数据不一致。

### 它不做什么

- **不会凭空造出你没买的服装**：服装本体由服务端下发，插件只处理皮肤与互动显示层。
- **不修改好感度、约会进度、经验、货币等数值**：这些补丁在参考实现里存在，本项目刻意不移植。
- **不发任何网络请求**：不登录、不上传、不下载资源。
- **不修改游戏本体文件**：只改运行时内存镜像，以及自己的 cfg / 选择记录。

## 二、安装

### 通过 GUI（推荐）

1. 打开 BD2ModManager，确认游戏目录；
2. 打开「一键启用」；
3. GUI 会自动安装 loader 和内置解锁插件，插件写到：

```
BepInEx/plugins/BD2SkinUnlock/BD2SkinUnlock.dll
```

4. 启动游戏。若之前装过其他解锁插件，请先移除，避免补丁互相冲突。

### 手动安装（一般不需要）

当前 Release 不单独提供 DLL。只有自行从源码构建时，才需要把生成的 `BD2SkinUnlock.dll` 放到：

```
BepInEx/plugins/BD2SkinUnlock/BD2SkinUnlock.dll
```

或直接放在 `BepInEx/plugins/` 下。重启游戏即可。

> 同时只能启用一个皮肤解锁插件。若之前装过其他同类插件，请先禁用或移除，否则补丁会互相冲突。

### 独立更新
插件单独发版，不需要重新下载整个主程序。GUI 的「更新检查」卡里有「插件更新」行：检查会读取 `plugin/plugin.json`，安装时只下载 `BD2SkinUnlock.dll`，校验 SHA256 后原子替换；游戏正在运行时先放到 `%LOCALAPPDATA%\BD2ModManager\plugin-update\pending-<版本>\`，退出游戏后再点一次安装。CLI 对应 `bd2mm plugin-update [游戏目录] [--check]`。

## 三、模块开关与配置

### 模块开关（推荐用 GUI）

首次启动会显示免责声明与模块勾选；之后可在主界面「功能模块」里调整。开关保存在：

```
%LOCALAPPDATA%\BD2ModManager\modules.ini
```

`[modules]` 段覆盖插件默认值，插件每轮巡检重新读取，改完无需重启游戏：

| 键 | 默认 | 说明 |
|---|---|---|
| `SkinUnlock` | `true` | 皮肤解锁总开关；关闭后插件不介入 |
| `Interactions` | `true` | 大厅互动扩展总开关 |
| `DisableCensorship` | `true` | 关闭客户端内容审查 |
| `CovenantExperimental` | `false` | 心契之约实验扩展（列表/布尔门/UI 恢复） |
| `AllCostumes` | `false` | 全服装本地镜像（高风险） |
| `AllCharacters` | `false` | 全角色本地镜像（当前仅预留） |
| `AllDatingCostumes` | `false` | 全情缘服装本地镜像（当前仅预留） |

高风险模块默认关闭；从关闭改为开启时，GUI 会弹出二次确认。`AllCostumes`/`AllCharacters`/`AllDatingCostumes` 只改客户端本地数据，服务器不会认可，可能出现界面异常或数据不一致，建议离线/单人使用。

### BepInEx 配置

配置文件：`BepInEx/config/com.suiren.bd2.skinunlock.cfg`

| 项 | 默认 | 说明 |
|---|---|---|
| `General.Enabled` | `true` | 总开关。关掉后插件不介入游戏，皮肤列表恢复原样（需重启生效） |
| `General.ReapplyIntervalSeconds` | `3` | 把已选皮肤写回服装的间隔（秒），范围 0.5–60。服务器同步会覆盖本地选择，靠这个恢复 |
| `Diagnostics.Verbose` | `false` | 输出每一轮巡检的细节，排查时打开 |
| `Interactions.UnlockAllInteractions` | `true` | 补全全部大厅互动入口（含非皮肤互动）；关闭后只补 `PrestigeSkinId != 0` 的皮肤场景 |
| `Interactions.BypassInteractionUnlock` | `true` | 放行互动解锁判定 |
| `Interactions.DisableCensorship` | `true` | 关闭客户端内容审查 |
| `Interactions.UnlockCovenantList` | `false` | 实验性：替换心契之约互动列表并放行互动布尔门；不同游戏版本可能界面异常 |
| `Skins.LobbyPortrait` | `true` | 大厅立绘使用当前选择的皮肤 |
| `Skins.LobbyDesignId` | `0` | 强制大厅立绘使用指定 designId；`0` = 按服装选择自动映射 |

选择记录单独存放：

```
BepInEx/config/com.suiren.bd2.skinunlock.selection.txt
```

格式为 `costumeId=designId`，每行一条。删除该文件会清空本地选择记录。

插件启动探针：

```
BepInEx/SkinUnlock.probe.log
```

记录生命周期、反射进度、补丁挂载数；排查「为什么没生效」先看它。

## 四、实现要点

插件编译期为 `netstandard2.0`，只引用 BepInEx、HarmonyX 与 UnityEngine 的编译引用，**不引用游戏程序集**。贴图回调处刻意不依赖 `Sprite` 类型，通过运行时类型 + 泛型闭包委托处理。游戏内部结构全部走反射，改结构时最多是功能失效，不会导致程序集加载失败。

### 基础六个 Harmony 补丁

| 补丁 | 目标 | 作用 |
|---|---|---|
| `GetConnected_Postfix` | `CostumeDBInfo.GetConnectedPrestigeSkinDBInfos` | 剔除脏条目，补全该服装的全部皮肤 |
| `SetSkin_Prefix` | `Database.SetSkin` | 拦下点击，记录本地选择，跳过服务器请求 |
| `AddSkin_Prefix` | `Database.AddPrestigeSkinDBInfo` | 拦下 `designId=0` / 表里没有的写入 |
| `LoadTexture_Prefix` | `SkinExchangePopupUI.LoadTexture` | 包装回调，0.5 秒未回调补 `null` |
| `TowerCostume_Postfix` ×2 | 救赎之塔（RogueLike）服装字典的两个单参插入方法 | 塔内每件服装写入后立即套用本地皮肤选择 |

### 互动扩展补丁（1.2.0）

这些补丁在能找到目标时全部挂载，具体是否生效由配置开关在每次调用时判断；目标缺失只写一条 `Complaint`，不会影响基础功能。

| 补丁 | 目标 | 作用 |
|---|---|---|
| `UnlockCheck_Postfix` | `CheckInteractionUnlock` | 互动解锁判定恒为 `true` |
| `Censorship_Postfix` | `CensorshipManager.IsCensorship` | 内容审查恒为 `false` |
| `SpineSetUI_Prefix` | `SpineInteractionUI.SetUI` | 把互动列表替换为全部 `Type=2` 互动的 `row.Id` |
| `True_Postfix` ×N | `SpineInteractionUI` 的 `Can/Interact/Unlock/Possible` 布尔 getter、`DatingFriends` 的约会服装谓词 | 放行互动按钮与约会服装查询 |
| `PortraitCostume_Postfix` | `UserDBInfo.get_UsePortraitCostumeId` | 大厅立绘槽位指向皮肤 |
| `PortraitDesign_Postfix` | `UserDBInfo.get_UsePortraitCostumeDesignTableID` | 大厅立绘映射到当前选择/强制 designId |
| `RestoreInteraction_Postfix` ×N | `SpineInteractionUI` 的 `(int,int)` 刷新方法 | 重新打开互动工具栏/缩放条，并替换操作提示 |

大厅互动道具仍在周期巡检里补进 `ItemDBInfo`，同样记账以便回滚；补丁只负责判定、列表与立绘，不写存档、不发请求。

### 救赎之塔（RogueLike）

救赎之塔使用独立的一套服装镜像：`RogueLikeManager` 里的 `Dictionary<long, CostumeDBInfo>`，每次收到 `EvilCastleRogueLikeInfoResponse` 都会清空重建，服务器下发的 `DesignId` 是 0。普通模式改的是全局服装字典，塔内读不到，所以 1.1.2 及以前在塔里不生效。

1.1.3 起：

1. 插件按「同时含 `Dictionary<long, CostumeDBInfo>` 与 `Dictionary<int, List<CostumeDBInfo>>` 静态字段」识别塔管理类；
2. 给它的两个单参插入方法挂 postfix，每件塔内服装一入字典就把选择表里的 `designId` 写进去；
3. 周期巡检再遍历塔字典回放一次，覆盖服务器后续同步；
4. 探针日志会显示 `塔字典 N 个`；进塔后还会打印 `救赎之塔服装镜像已建立：N 件`，巡检行的 `塔衣=N`、`塔同步=N` 应大于 0。

### 为 BD2 做的两个特殊处理

1. **补丁在 `Awake` 挂载，周期任务用 `Application.onBeforeRender` 驱动。**
   BD2 会在 `Awake` 后约 0.66 秒销毁 BepInEx 的 manager 对象，此后 `Update` 一次都不会被调用。任何依赖 `Update` 轮询的 BepInEx 插件在这款游戏里都不会工作。`onBeforeRender` 是 Unity 的静态渲染回调，不依赖 GameObject，对象被销毁后仍会触发。
2. **皮肤目录延迟构建。**
   设计表要等游戏下发；补丁被调用时若目录还没建好，会现场读表建一次，不再依赖定时器。目录构建完成后也不在补丁调用栈里改字典，避免集合修改异常。

### 兼容性取舍

| 取舍点 | 做法 |
|---|---|
| 编译期依赖 | 全反射；游戏改结构只会让功能失效，不会让程序集加载失败 |
| 找字典 | 解析一次并缓存，不反复遍历程序集 |
| 周期性清理 | 只校验自己补过的条目，代价 O(补入条数) |
| 出错处理 | 交还原方法，游戏照常走 |
| 退出时 | 移除注入条目 + 撤销补丁 |
| Harmony 版本 | 反射调用 `Patch`，按运行时实际签名，不绑死编译期那一版 |

## 五、验证到哪一步

### 离线验证（106 项）

`tests/BD2ModManager.SkinUnlock.Tests` 用一套假游戏结构（`Proto.Net.CostumeDBInfo` / `Proto.Net.PrestigeSkinDBInfo` / `Proto.Design.common.PrestigeSkinTable` / `RawDataManager`，字段布局按真实客户端摆）把整条链路跑一遍：

```bash
dotnet run --project tests/BD2ModManager.SkinUnlock.Tests -c Release
```

覆盖：

- 反射解析、读设计表；
- 注入条目、补全列表、去重；
- 拦截点击、单一选中、选择记录；
- 服务器覆盖后回放、取消、回滚；
- 撤销补丁；
- 贴图正常回调 / 超时补 `null`；
- `designId=0` 与未知 id 的拦截；
- 大厅互动场景的注入与回滚；
- 全部互动条目注入、心契之约 `row.Id` 过滤、`InvenIndex/Count/TimeValue/KeepFlag` 字段；
- 互动解锁放行、内容审查关闭、心契之约列表替换；
- 大厅立绘自动映射与 `LobbyDesignId` 强制覆盖；
- 救赎之塔字典解析、塔服装 postfix 套用与周期回放。

CI 在 GitHub Actions 上跑真实 Harmony 挂载（不是直接调用补丁方法），基础补丁与互动扩展补丁都挂到真实方法上：

```
log  补丁已挂上：查询服装皮肤 → CostumeDBInfo.GetConnectedPrestigeSkinDBInfos
log  补丁已挂上：切换皮肤 → Database.SetSkin
log  补丁已挂上：互动解锁判定 → RawDataManager.CheckInteractionUnlock
log  补丁已挂上：内容审查 → CensorshipManager.IsCensorship
全部通过：106 项检查。
```

### 真机验证

真机测试中补出了 **17 件服装对应的 17 套皮肤与 10 个大厅互动场景**，玩家确认未拥有的皮肤出现在「更换皮肤」弹窗里。1.1.3 真机启动日志确认 `塔字典 1 个`、`补丁挂上 6 个`；2026-11-11 进入救赎之塔实测通过，日志出现 `救赎之塔服装镜像已建立：7 件`，玩家确认塔内皮肤已生效。

### 没有覆盖的部分

真实游戏里的字段名、加载时机、弹窗行为只能进游戏验证。插件把生命周期、反射进度与补丁挂载数写进 `BepInEx\SkinUnlock.probe.log`，解析不到时会说清卡在哪一步（`Step`）和原因（`Problem`）。

## 六、故障排查

| 现象 | 排查 |
|---|---|
| 游戏里完全没效果 | 先看 `BepInEx/SkinUnlock.probe.log`：确认 `Awake` 是否执行、基础 6 个补丁是否挂上、卡在哪一步 |
| 互动/立绘没效果 | 看探针日志的「互动门 / 约会谓词」数量；若为 0，说明当前游戏版本改了方法名，`Complaint` 会写出缺哪一项 |
| 只有救赎之塔没效果 | 看日志是否有 `塔字典 1 个`；进塔后巡检行的 `塔同步` 是否大于 0。若为 0，说明塔服装插入方法没被调用或没挂上 |
| 日志里没有插件输出 | 可能是 BepInEx 日志通道被杀软短暂独占；探针日志是绕开 BepInEx 直接写文件的，以它为准 |
| 装完提示两个解锁插件 | 到 `BepInEx/plugins` 下禁用/删除旧插件，只保留 `BD2SkinUnlock` |
| 改了 `Enabled` 没变化 | 需要重启游戏 |
| 选择被服务器冲掉 | 插件会按 `ReapplyIntervalSeconds` 回放；若仍失效，确认插件仍在运行、间隔未被改得过大 |
| 卸载后留下 `SkinUnlock.probe.log` | 用 GUI 的「清理残留」或重新开关一次 Mod 支持；卸载 loader 时会自动清 |

## 七、许可

本插件以 **MIT** 协议开源，源码在 `src/BD2ModManager.SkinUnlock/`。它不包含任何第三方闭源代码；BepInEx / HarmonyX 仅作为运行时依赖，遵循其各自许可（见 [COMPLIANCE.md](COMPLIANCE.md)）。
