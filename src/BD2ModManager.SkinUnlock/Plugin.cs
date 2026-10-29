using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 皮肤解锁 —— 独立实现，MIT。
    ///
    /// 做的事：
    ///   1. 让「已拥有的服装」能选到它全部皮肤（数据来自游戏自带设计表，不硬编码）；
    ///   2. 点击皮肤时只改本地内存，不向服务器发切换请求；
    ///   3. 弹窗贴图 0.5 秒不回调就补 null，避免未拥有的皮肤一直转圈；
    ///   4. 拦下 designId=0 / 设计表里查不到的皮肤条目；
    ///   5. 大厅互动场景（PrestigeSkinId != 0 的道具）一并补出来。
    ///
    /// 全程不发网络请求，只动本地数据镜像；退出时把自己加进去的条目清理掉。
    /// </summary>
    [BepInPlugin(PluginGuid, "SkinUnlock", Version)]
    public sealed class SkinUnlockPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.suiren.bd2.skinunlock";
        public const string Version = "1.1.1";

        private static ManualLogSource _log;

        private readonly GameRefs _refs = new GameRefs();
        private readonly SkinCatalog _catalog = new SkinCatalog();
        private readonly SkinState _state = new SkinState();

        private readonly Stopwatch _heartbeat = Stopwatch.StartNew();
        private readonly Stopwatch _work = Stopwatch.StartNew();

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _reapplyInterval;
        private ConfigEntry<bool> _verbose;

        private Harmony _harmony;
        private string _selectionPath;

        private bool _patched;
        private bool _ready;          // 解析 + 目录都就绪
        private bool _stopped;
        private bool _shutdown;
        private int _attempts;
        private int _lastInjected;

        private int _updateCount;
        private int _renderCount;
        private bool _driveFaulted;

        private string _lastResolveReason = "";
        private string _lastCatalogReason = "";

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            _log = Logger;
            Diag.Open("Awake 开始（v" + Version + "）");

            BindConfig();
            Diag.Note($"配置：Enabled={_enabled.Value} Verbose={_verbose.Value} 间隔={_reapplyInterval.Value}s");

            if (!_enabled.Value)
            {
                LogInfo("总开关是关闭的，插件不介入游戏。");
                Diag.Note("总开关关闭，插件退出");
                return;
            }

            Patches.LogInfo = LogInfo;
            Patches.LogWarn = LogWarn;
            Patches.Refs = _refs;
            Patches.Catalog = _catalog;
            Patches.State = _state;
            Patches.SaveSelection = SaveSelection;

            _selectionPath = Path.Combine(Paths.ConfigPath, PluginGuid + ".selection.txt");
            LoadSelection();

            _harmony = new Harmony(PluginGuid);

            // 补丁被调用时自己把目录建起来，不依赖 Update 是否在跑
            Patches.EnsureCatalog = EnsureCatalog;

            LogInfo($"v{Version} 已加载，等待游戏数据就绪。配置：{Config.ConfigFilePath}");

            // 解析与挂补丁一律放在 Awake：补丁一旦挂上就永久生效，
            // 之后每帧有没有人调 Update 都不影响它工作。
            Bootstrap();

            Diag.Note("Awake 完成");
        }

        /// <summary>
        /// 把「解析 + 挂补丁」提前到 Awake。
        /// 皮肤设计表要等游戏下发，所以目录构建尽力而为，建不出来就交给补丁自己触发。
        /// </summary>
        private void Bootstrap()
        {
            if (!_refs.TryResolve())
            {
                Diag.Note($"Awake 解析未就绪：step={_refs.Step} / {_refs.Problem}，交给 Update 重试");
                LogWarn($"Awake 时还读不到游戏内部结构（{_refs.Step}）：{_refs.Problem}，之后继续重试。");
                return;
            }

            Diag.Note("Awake 解析成功：" + _refs.Describe());

            var mounted = Patches.Install(_harmony, _refs);
            _patched = true;
            Patches.Active = true;
            Diag.Note($"补丁挂上 {mounted} 个");

            if (_catalog.TryBuild(_refs, LogInfo, LogWarn))
            {
                _ready = true;
                Diag.Note($"目录就绪：{_catalog.CostumeCount} 件服装 / {_catalog.SkinCount} 套皮肤");
                _work.Restart();
                Tick();
            }
            else
            {
                Diag.Note("设计表还没下发，目录等补丁触发时再建");
            }
        }

        /// <summary>补丁里调用：目录还没建好就现场建一次（此时游戏正在查皮肤，数据一定在）。</summary>
        private bool EnsureCatalog()
        {
            if (_catalog.Ready) return true;
            if (!_enabled.Value || _shutdown) return false;

            var ok = _catalog.TryBuild(_refs, LogInfo, LogWarn);
            if (ok)
            {
                _ready = true;
                Diag.Note($"目录由补丁触发建好：{_catalog.CostumeCount} 件服装 / {_catalog.SkinCount} 套皮肤");

                // 这里不顺手跑 Tick：补丁正处在游戏遍历集合的调用栈里，
                // 此时改动字典容易触发集合修改异常，交给下一拍巡检。
                _work.Restart();
            }
            return ok;
        }

        private void Start() => Diag.Note("Start 被调用");

        private void OnEnable()
        {
            Diag.Note("OnEnable 被调用");

            // BD2 会在启动阶段把 BepInEx 的 manager 对象销毁掉（实测 OnDestroy 在
            // Awake 之后 0.6 秒就到），之后 MonoBehaviour 的 Update 再也不会被调用。
            // onBeforeRender 是 Unity 的静态渲染回调，只要游戏在出画面就会触发，
            // 不依赖任何 GameObject，正好用来顶替 Update 驱动周期任务。
            Application.onBeforeRender -= OnBeforeRender;
            Application.onBeforeRender += OnBeforeRender;
        }

        private void OnDisable() => Diag.Note("OnDisable 被调用（对象被禁用或销毁）");

        private void Update()
        {
            _updateCount++;
            if (_updateCount == 1 || _updateCount == 2 || _updateCount == 10)
                Diag.Note($"Update 第 {_updateCount} 次");

            Drive("Update");
        }

        private void OnBeforeRender()
        {
            _renderCount++;
            if (_renderCount == 1 || _renderCount == 2 || _renderCount == 60 || _renderCount == 600)
                Diag.Note($"onBeforeRender 第 {_renderCount} 次（这是替代 Update 的驱动）");

            Drive("onBeforeRender");
        }

        /// <summary>两个驱动入口共用，靠心跳计时器限频，谁先跑到算谁的。</summary>
        private void Drive(string source)
        {
            if (_stopped || _shutdown || _driveFaulted) return;

            try
            {
                DriveCore();
            }
            catch (Exception e)
            {
                // 没有这层兜底时，一帧异常会每帧重抛，插件彻底失效且不留痕迹
                _driveFaulted = true;
                Diag.Note($"驱动（{source}）抛异常，已停止巡检：" + e);
                LogWarn("巡检抛异常，插件已停止（详情见 BepInEx\\SkinUnlock.probe.log）：" + e.GetType().Name + " " + e.Message);
            }
        }

        private void DriveCore()
        {
            // 贴图兜底要按帧走：0.5 秒的窗口用 500ms 心跳去判太粗
            TextureGuard.Tick(TextureGuard.Now);

            if (_heartbeat.Elapsed.TotalMilliseconds < 500) return;
            _heartbeat.Restart();

            if (!_ready)
            {
                TryInitialize();
                return;
            }

            if (!_enabled.Value) return;

            var interval = Math.Max(0.5f, _reapplyInterval.Value);
            if (_work.Elapsed.TotalSeconds < interval) return;
            _work.Restart();

            Tick();
        }

        private void OnApplicationQuit()
        {
            Diag.Note("OnApplicationQuit");
            Shutdown();
        }

        private void OnDestroy()
        {
            Diag.Note("OnDestroy");

            // 这里千万不能 Shutdown：BD2 会销毁 manager 对象，但游戏还在正常运行。
            // 一旦在此时撤销补丁，插件就自己把自己废了（实测症状就是「补丁已挂上」
            // 之后紧跟「补丁已撤销」，然后游戏里毫无效果）。
            // 补丁留到进程结束即可，退出时由 OnApplicationQuit 统一收尾。
        }

        // ---------------------------------------------------------------- 初始化

        /// <summary>
        /// 游戏数据是异步下发的，所以解析要重试。每次重试都很快：
        /// 已经解析成功的成员全部缓存，不会重新扫程序集。
        /// </summary>
        private void TryInitialize()
        {
            _attempts++;

            if (!_refs.TryResolve())
            {
                Trace(ref _lastResolveReason, $"解析未就绪：step={_refs.Step} / {_refs.Problem}");

                if (_attempts == 1 || _attempts % 20 == 0)
                    LogWarn($"还没法读取游戏内部结构（{_refs.Step}）：{_refs.Problem}");

                if (_attempts >= 120)
                {
                    LogError("多次尝试后仍无法读取游戏内部结构，插件停止工作。" +
                          "多半是游戏更新改了内部字段，请反馈日志。");
                    Diag.Note("放弃：120 次仍未解析出游戏结构");
                    _stopped = true;
                }
                return;
            }

            if (!_catalog.TryBuild(_refs, LogInfo, LogWarn))
            {
                // 皮肤设计表还没下发，下一拍再试
                Trace(ref _lastCatalogReason, "结构就绪，但皮肤设计表还没下发");
                if (_attempts % 20 == 0) LogInfo("等待游戏加载皮肤设计表…");
                return;
            }

            Diag.Note($"解析#{_attempts} 全部就绪：{_refs.Describe()}");
            LogInfo("内部结构：" + _refs.Describe());

            if (!_patched)
            {
                var mounted = Patches.Install(_harmony, _refs);
                _patched = true;
                Diag.Note($"补丁挂上 {mounted} 个");
            }

            Patches.Active = true;
            _ready = true;
            Diag.Note($"就绪：{_catalog.CostumeCount} 件服装 / {_catalog.SkinCount} 套皮肤");
            LogInfo($"就绪：{_catalog.CostumeCount} 件已拥有服装对应 {_catalog.SkinCount} 套皮肤。");

            // 立刻跑一轮，别等下一个间隔
            _work.Restart();
            Tick();
        }

        /// <summary>
        /// 只在「卡住的原因」变化时落盘，否则玩家挂机一小时能刷出几十万行。
        /// 原因不变也要留下心跳，好区分「还在重试」和「已经停了」。
        /// </summary>
        private static void Trace(ref string last, string reason)
        {
            if (reason != last)
            {
                last = reason;
                Diag.Note(reason);
            }
        }

        private void Tick()
        {
            try
            {
                var injected = _state.EnsureInjected(_refs, _catalog);
                if (injected > 0)
                {
                    if (_verbose.Value || injected != _lastInjected)
                        LogInfo($"补入皮肤条目 {injected} 条（当前共 {_state.InjectedCount} 条）");
                    _lastInjected = injected;
                }

                // 清掉 designId=0 的默认皮肤与设计表里没有的脏条目（每轮巡检清一次）
                var cleaned = _state.CleanupInvalid(_refs);
                if (cleaned > 0 && _verbose.Value)
                    LogInfo($"清理脏皮肤条目 {cleaned} 条（退出时会放回）");

                // 大厅互动场景：Type=25 的道具，未购入时也要出现在大厅
                var scenes = InteractionScenes.Inject(_refs);
                if (scenes > 0)
                    LogInfo($"补入大厅互动场景 {scenes} 个（当前共 {InteractionScenes.InjectedCount} 个）");

                var reapplied = _state.ReapplySelection(_refs);
                if (reapplied > 0 && _verbose.Value)
                    LogInfo($"已把 {reapplied} 件服装的选择写回本地数据");

                if (injected > 0 || cleaned > 0 || scenes > 0 || reapplied > 0)
                {
                    Diag.Note($"巡检：注入={injected}(共{_state.InjectedCount}) 清理={cleaned} " +
                              $"场景={scenes}(共{InteractionScenes.InjectedCount}) 回放={reapplied}");
                }

                if (_verbose.Value)
                {
                    var stale = _state.VerifyInjected(_refs);
                    if (stale > 0) LogWarn($"有 {stale} 条注入条目已失效，将在下一轮重建");
                }
            }
            catch (Exception e)
            {
                LogWarn("巡检出错：" + e.Message);
            }
        }

        private void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            Patches.Active = false;

            try { _state.Rollback(_refs); } catch { }
            try { InteractionScenes.Rollback(_refs); } catch { }
            TextureGuard.Clear();

            try
            {
                Patches.Uninstall(_harmony);
                _harmony = null;
            }
            catch { }

            if (_patched) LogInfo("已退出：注入的条目已移除，补丁已撤销。");
            Diag.Note($"Shutdown 完成（patch={_patched}）");
            Diag.Close();
        }

        // ---------------------------------------------------------------- 配置

        private void BindConfig()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "总开关。关掉后插件不介入游戏，皮肤列表恢复原样（需要重启游戏）。");

            _reapplyInterval = Config.Bind("General", "ReapplyIntervalSeconds", 3f,
                new ConfigDescription(
                    "把已选皮肤写回服装的间隔（秒）。服务器同步会覆盖本地选择，靠这个恢复。",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            _verbose = Config.Bind("Diagnostics", "Verbose", false,
                "输出每一轮巡检的细节，排查问题时打开。");
        }

        // ---------------------------------------------------------------- 选择记录

        private void LoadSelection()
        {
            try
            {
                if (!File.Exists(_selectionPath)) return;

                var map = new Dictionary<int, int>();
                foreach (var raw in File.ReadAllLines(_selectionPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var cut = line.IndexOf('=');
                    if (cut <= 0) continue;

                    if (int.TryParse(line.Substring(0, cut), out var costumeId) &&
                        int.TryParse(line.Substring(cut + 1), out var designId) &&
                        costumeId != 0 && designId != 0)
                    {
                        map[costumeId] = designId;
                    }
                }

                _state.ReplaceSelection(map);
                if (map.Count > 0) LogInfo($"已载入 {map.Count} 条皮肤选择。");
            }
            catch (Exception e)
            {
                LogWarn("读取皮肤选择失败：" + e.Message);
            }
        }

        private void SaveSelection()
        {
            try
            {
                var lines = new List<string>
                {
                    "# " + PluginGuid + " 选择的皮肤",
                    "# 格式：costumeId=designId",
                };

                foreach (var kv in _state.Selection)
                    lines.Add(kv.Key + "=" + kv.Value);

                var dir = Path.GetDirectoryName(_selectionPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllLines(_selectionPath, lines.ToArray());
            }
            catch (Exception e)
            {
                LogWarn("保存皮肤选择失败：" + e.Message);
            }
        }

        // ---------------------------------------------------------------- 杂项

        internal static void LogInfo(string message)
        {
            try { _log?.LogInfo("[SkinUnlock] " + message); } catch { }
        }

        internal static void LogWarn(string message)
        {
            try { _log?.LogWarning("[SkinUnlock] " + message); } catch { }
        }

        internal static void LogError(string message)
        {
            try { _log?.LogError("[SkinUnlock] " + message); } catch { }
        }
    }
}
