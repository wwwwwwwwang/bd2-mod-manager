# 内置皮肤解锁插件

`BD2SkinUnlock.dll` 是本仓库自研的 BepInEx 5 插件（MIT），已内嵌在 GUI 中，不单独发布。

一句话：**不改变游戏数值、不联网，仅让已拥有服装的皮肤在本地显示出来。**

## 一、它做什么

只对**你已经拥有的服装**生效：

1. **补全皮肤列表**：从游戏自带的 `PrestigeSkinTable` 设计表读出「这件服装有哪些皮肤」，把未购买的皮肤条目补进本地列表；不硬编码任何 id，游戏更新后新增的皮肤会自动出现。
2. **点击只改本地内存**：拦下 `Database.SetSkin`，不向服务器发送切换请求；选择记录持久化在本地文件，游戏退出时只移除注入条目并撤销补丁；关闭总开关（重启后）插件不再介入。
3. **服务器覆盖后回放**：服务器同步会把本地选择冲掉，插件按固定间隔（默认 3 秒）把已选皮肤写回服装。
4. **贴图回调兜底**：皮肤弹窗取头像时，`LoadTexture` 0.5 秒没回调就补 `null`，未拥有的皮肤不会一直转圈。
5. **拦下无效默认皮肤**：`designId=0` 或设计表里查不到的条目不再写进皮肤字典。
6. **补全大厅互动场景**：把 `LobbySettingItemTable` 里 `PrestigeSkinId != 0` 的道具补进道具字典（Type=25），并记账以便退出时还原。
7. **退出时回滚**：移除自己补入的条目，把清理脏条目时移走的游戏原始条目放回去；`OnApplicationQuit` 撤销补丁。

### 它不做什么

- **不会凭空造出你没买的服装**：服装本体由服务端下发，插件只处理皮肤显示层。
- **不发任何网络请求**：不登录、不上传、不下载资源。
- **不修改游戏本体文件**：只改运行时内存镜像，以及自己的 cfg / 选择记录。
- **不改变数值**：攻击、防御、货币、关卡奖励等一律不碰。

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

## 三、配置

配置文件：`BepInEx/config/com.suiren.bd2.skinunlock.cfg`

| 项 | 默认 | 说明 |
|---|---|---|
| `General.Enabled` | `true` | 总开关。关掉后插件不介入游戏，皮肤列表恢复原样（需重启生效） |
| `General.ReapplyIntervalSeconds` | `3` | 把已选皮肤写回服装的间隔（秒），范围 0.5–60。服务器同步会覆盖本地选择，靠这个恢复 |
| `Diagnostics.Verbose` | `false` | 输出每一轮巡检的细节，排查时打开 |

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

### 四个 Harmony 补丁

| 补丁 | 目标 | 作用 |
|---|---|---|
| `GetConnected_Postfix` | `CostumeDBInfo.GetConnectedPrestigeSkinDBInfos` | 剔除脏条目，补全该服装的全部皮肤 |
| `SetSkin_Prefix` | `Database.SetSkin` | 拦下点击，记录本地选择，跳过服务器请求 |
| `AddSkin_Prefix` | `Database.AddPrestigeSkinDBInfo` | 拦下 `designId=0` / 表里没有的写入 |
| `LoadTexture_Prefix` | `SkinExchangePopupUI.LoadTexture` | 包装回调，0.5 秒未回调补 `null` |

大厅互动场景不靠补丁，而是在周期巡检里把 `LobbySettingItemTable` 的道具补进 `ItemDBInfo`，同样记账以便回滚。

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

### 离线验证（62 项）

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
- 大厅互动场景的注入与回滚。

CI 在 GitHub Actions 上跑真实 Harmony 挂载（不是直接调用补丁方法），四个补丁都挂到真实方法上：

```
log  补丁已挂上：查询服装皮肤 → CostumeDBInfo.GetConnectedPrestigeSkinDBInfos
log  补丁已挂上：切换皮肤 → Database.SetSkin
[OK] 四个补丁都挂到了真实方法上
全部通过：62 项检查。
```

### 真机验证

真机测试中补出了 **16 件服装对应的 16 套皮肤与 10 个大厅互动场景**，玩家确认未拥有的皮肤出现在「更换皮肤」弹窗里。

### 没有覆盖的部分

真实游戏里的字段名、加载时机、弹窗行为只能进游戏验证。插件把生命周期、反射进度与补丁挂载数写进 `BepInEx\SkinUnlock.probe.log`，解析不到时会说清卡在哪一步（`Step`）和原因（`Problem`）。

## 六、故障排查

| 现象 | 排查 |
|---|---|
| 游戏里完全没效果 | 先看 `BepInEx/SkinUnlock.probe.log`：确认 `Awake` 是否执行、补丁挂载数是否为 4、卡在哪一步 |
| 日志里没有插件输出 | 可能是 BepInEx 日志通道被杀软短暂独占；探针日志是绕开 BepInEx 直接写文件的，以它为准 |
| 装完提示两个解锁插件 | 到 `BepInEx/plugins` 下禁用/删除旧插件，只保留 `BD2SkinUnlock` |
| 改了 `Enabled` 没变化 | 需要重启游戏 |
| 选择被服务器冲掉 | 插件会按 `ReapplyIntervalSeconds` 回放；若仍失效，确认插件仍在运行、间隔未被改得过大 |
| 卸载后留下 `SkinUnlock.probe.log` | 用 GUI 的「清理残留」或重新开关一次 Mod 支持；卸载 loader 时会自动清 |

## 七、许可

本插件以 **MIT** 协议开源，源码在 `src/BD2ModManager.SkinUnlock/`。它不包含任何第三方闭源代码；BepInEx / HarmonyX 仅作为运行时依赖，遵循其各自许可（见 [COMPLIANCE.md](COMPLIANCE.md)）。
