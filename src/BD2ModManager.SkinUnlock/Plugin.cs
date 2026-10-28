using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 皮肤解锁 —— 独立实现，MIT。
    ///
    /// 做的事只有两件：
    ///   1. 让「已拥有的服装」能选到它全部皮肤（数据来自游戏自带设计表，不硬编码）；
    ///   2. 点击皮肤时只改本地内存，不向服务器发切换请求。
    ///
    /// 全程不发网络请求，只动本地数据镜像；退出时把自己加进去的条目清理掉。
    /// </summary>
    [BepInPlugin(PluginGuid, "SkinUnlock", Version)]
    public sealed class SkinUnlockPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.suiren.bd2.skinunlock";
        public const string Version = "1.0.0";

        /// <summary>同类插件。两个一起装会互相覆盖同一批补丁，只保留一个。</summary>
        private const string ConflictGuid = "com.example.legacy";
        private const string LegacyConfigName = "第三方闭源插件.cfg";

        private static ManualLogSource _log;

        private readonly GameRefs _refs = new GameRefs();
        private readonly SkinCatalog _catalog = new SkinCatalog();
        private readonly SkinState _state = new SkinState();

        private readonly Stopwatch _heartbeat = Stopwatch.StartNew();
        private readonly Stopwatch _work = Stopwatch.StartNew();

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _reapplyInterval;
        private ConfigEntry<bool> _migrateLegacy;
        private ConfigEntry<bool> _verbose;

        private Harmony _harmony;
        private string _selectionPath;

        private bool _patched;
        private bool _ready;          // 解析 + 目录都就绪
        private bool _stopped;
        private bool _shutdown;
        private int _attempts;
        private int _conflictChecks = 20;
        private int _lastInjected;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            _log = Logger;
            BindConfig();

            if (!_enabled.Value)
            {
                LogInfo("总开关是关闭的，插件不介入游戏。");
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

            if (_migrateLegacy.Value) MigrateLegacySelection();

            if (FindConflict(out var other))
            {
                LogWarn($"检测到同类插件「{other}」同时在跑（{ConflictGuid}）。两套补丁会改同一批数据，" +
                     "本插件已自动停用，请只保留一个。");
                _stopped = true;
                return;
            }

            _harmony = new Harmony(PluginGuid);
            LogInfo($"v{Version} 已加载，等待游戏数据就绪。配置：{Config.ConfigFilePath}");
        }

        private void Update()
        {
            if (_stopped || _shutdown) return;
            if (_heartbeat.Elapsed.TotalMilliseconds < 500) return;
            _heartbeat.Restart();

            // 插件加载顺序不固定，同类插件可能比我们晚一步，启动后多看几次
            if (_conflictChecks > 0)
            {
                _conflictChecks--;
                if (FindConflict(out var other))
                {
                    LogWarn($"检测到同类插件「{other}」，本插件停用并撤销自己的补丁。");
                    Shutdown();
                    _stopped = true;
                    return;
                }
            }

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

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy() => Shutdown();

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
                if (_attempts == 1 || _attempts % 20 == 0)
                    LogWarn($"还没法读取游戏内部结构（{_refs.Step}）：{_refs.Problem}");

                if (_attempts >= 120)
                {
                    LogError("多次尝试后仍无法读取游戏内部结构，插件停止工作。" +
                          "多半是游戏更新改了内部字段，请反馈日志。");
                    _stopped = true;
                }
                return;
            }

            if (!_catalog.TryBuild(_refs, LogInfo, LogWarn))
            {
                // 皮肤设计表还没下发，下一拍再试
                if (_attempts % 20 == 0) LogInfo("等待游戏加载皮肤设计表…");
                return;
            }

            LogInfo("内部结构：" + _refs.Describe());

            if (!_patched)
            {
                Patches.Install(_harmony, _refs);
                _patched = true;
            }

            Patches.Active = true;
            _ready = true;
            LogInfo($"就绪：{_catalog.CostumeCount} 件已拥有服装对应 {_catalog.SkinCount} 套皮肤。");

            // 立刻跑一轮，别等下一个间隔
            _work.Restart();
            Tick();
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

                var reapplied = _state.ReapplySelection(_refs);
                if (reapplied > 0 && _verbose.Value)
                    LogInfo($"已把 {reapplied} 件服装的选择写回本地数据");

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

            try
            {
                Patches.Uninstall(_harmony);
                _harmony = null;
            }
            catch { }

            if (_patched) LogInfo("已退出：注入的条目已移除，补丁已撤销。");
        }

        // ---------------------------------------------------------------- 配置

        private void BindConfig()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "总开关。关掉后插件不介入游戏，皮肤列表恢复原样（需要重启游戏）。");

            _reapplyInterval = Config.Bind("General", "ReapplyIntervalSeconds", 2f,
                new ConfigDescription(
                    "把已选皮肤写回服装的间隔（秒）。服务器同步会覆盖本地选择，靠这个恢复。",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            _migrateLegacy = Config.Bind("General", "MigrateLegacyConfig", true,
                "首次运行时，把旧插件 第三方闭源插件.cfg 里的皮肤选择导入过来。");

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

        /// <summary>从旧插件迁移选择，迁完把文件改名，避免每次启动重复导入。</summary>
        private void MigrateLegacySelection()
        {
            var legacy = Path.Combine(Paths.ConfigPath, LegacyConfigName);
            if (!File.Exists(legacy)) return;

            try
            {
                var imported = 0;
                var map = new Dictionary<int, int>();

                foreach (var raw in File.ReadAllLines(legacy))
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
                        imported++;
                    }
                }

                if (imported > 0)
                {
                    var merged = new Dictionary<int, int>();
                    foreach (var kv in _state.Selection) merged[kv.Key] = kv.Value;
                    foreach (var kv in map) merged[kv.Key] = kv.Value;   // 旧插件的记录优先

                    _state.ReplaceSelection(merged);
                    SaveSelection();
                    LogInfo($"已从旧插件导入 {imported} 条皮肤选择。");
                }

                File.Move(legacy, legacy + ".imported");
            }
            catch (Exception e)
            {
                LogWarn("迁移旧配置失败（不影响使用）：" + e.Message);
            }
        }

        // ---------------------------------------------------------------- 杂项

        private static bool FindConflict(out string name)
        {
            name = null;
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    if (kv.Key != ConflictGuid) continue;
                    name = kv.Value?.Metadata?.Name ?? kv.Key;
                    return true;
                }
            }
            catch { }
            return false;
        }

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
