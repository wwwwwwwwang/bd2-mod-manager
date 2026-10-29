using System.Diagnostics;
using System.Text.Json;

namespace BD2ModManager.Core;

/// <summary>读取游戏目录里已安装的 loader 状态（只读，不修改任何文件）。</summary>
public static class LoaderInspector
{
    public static LoaderStatus Inspect(GameInstall game, StateStore store)
    {
        var warnings = new List<string>();

        var bepDir = Path.Combine(game.Root, "BepInEx");
        var winhttp = Path.Combine(game.Root, "winhttp.dll");
        var doorstopIni = Path.Combine(game.Root, "doorstop_config.ini");
        var doorstopVer = Path.Combine(game.Root, ".doorstop_version");

        bool present = Directory.Exists(bepDir) || File.Exists(winhttp) || File.Exists(doorstopIni);
        bool corePresent = File.Exists(winhttp) || File.Exists(Path.Combine(bepDir, "core", "BepInEx.dll"));

        // 只有「本工具能清的 loader 运行时产物」才算痕迹。
        // plugins 里放着的插件不算：那是用户的东西，删不掉，也没法让目录消失。
        bool hasTraces = !corePresent && (
            File.Exists(winhttp) ||
            File.Exists(doorstopIni) ||
            File.Exists(doorstopVer) ||
            Directory.Exists(Path.Combine(bepDir, "cache")) ||
            Directory.Exists(Path.Combine(bepDir, "core")) ||
            File.Exists(Path.Combine(bepDir, "config", "BepInEx.cfg")) ||
            File.Exists(Path.Combine(bepDir, "config", "com.bepis.bepinex.configurationmanager.cfg")) ||
            HasLogFiles(bepDir));

        // 部分安装的判定
        if (present && !corePresent)
        {
            // 核心已经不在了，加载不了任何插件，也谈不上「他人安装」。
            // 但要区分两类：还有东西可清，还是只剩插件。
            if (hasTraces)
                warnings.Add("loader 核心已不在，剩余的是缓存与日志残留，可一键清理");
        }
        else if (present)
        {
            if (!File.Exists(winhttp)) warnings.Add("缺少 winhttp.dll —— loader 不会生效");
            if (!Directory.Exists(Path.Combine(bepDir, "core")))
                warnings.Add("缺少 BepInEx/core —— loader 不完整");
            if (!File.Exists(doorstopIni)) warnings.Add("缺少 doorstop_config.ini");
        }

        string? doorVer = null;
        if (File.Exists(doorstopVer))
        {
            try { doorVer = File.ReadAllText(doorstopVer).Trim(); } catch { }
        }

        string? bepVer = null;
        var bepDll = Path.Combine(bepDir, "core", "BepInEx.dll");
        if (File.Exists(bepDll))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(bepDll);
                // ProductVersion 常带 "+<提交哈希>" 或 " (build ...)"，只取版本号本身
                bepVer = NormalizeVersion(vi.ProductVersion) ?? NormalizeVersion(vi.FileVersion);
            }
            catch
            {
                warnings.Add("无法读取 BepInEx.dll 版本");
            }
        }

        var state = store.TryLoad(game.Root);

        return new LoaderStatus
        {
            Present = present,
            CorePresent = corePresent,
            HasTraces = hasTraces,
            DoorstopVersion = doorVer,
            BepInExVersion = bepVer,
            ManagedByUs = state != null,
            DetectedPackageVersion = state?.PackageVersion,
            Plugins = ListPlugins(bepDir),
            Warnings = warnings
        };
    }

    private static bool HasLogFiles(string bepDir)
    {
        try { return Directory.EnumerateFiles(bepDir, "LogOutput.log*").Any(); }
        catch { return false; }
    }

    /// <summary>去掉 "+hash"、" (build)" 之类的后缀，只留版本号。</summary>
    private static string? NormalizeVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var cut = v.IndexOfAny(new[] { '+', ' ' });
        return cut > 0 ? v[..cut] : v;
    }

    public static List<PluginEntry> ListPlugins(string bepDir)
    {
        var list = new List<PluginEntry>();
        var pluginsDir = Path.Combine(bepDir, "plugins");
        if (!Directory.Exists(pluginsDir)) return list;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories); }
        catch { return list; }

        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            list.Add(new PluginEntry(f, name, true, size));

            // .dll.disabled 视为禁用
            var disabled = f + ".disabled";
            if (File.Exists(disabled))
            {
                list[^1] = list[^1] with { Enabled = false };
            }
        }

        // 被改名为 .disabled 的插件本体不会命中 *.dll，单独补齐
        try
        {
            foreach (var f in Directory.EnumerateFiles(pluginsDir, "*.dll.disabled", SearchOption.AllDirectories))
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                list.Add(new PluginEntry(f, Path.GetFileName(f)[..^".disabled".Length], false, size));
            }
        }
        catch { }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// 状态文件存放位置。刻意放在游戏目录之外，
/// 这样即使 BepInEx 目录被手工删掉，仍能读到卸载所需的信息。
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string BaseDir { get; }

    public StateStore(string? baseDir = null)
    {
        BaseDir = baseDir ?? DefaultBaseDir();
    }

    public static string DefaultBaseDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "BD2ModManager");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(home, ".local", "share", "BD2ModManager")
            : Path.Combine(xdg, "BD2ModManager");
    }

    public string StateDir => Path.Combine(BaseDir, "state");
    public string BackupRoot => Path.Combine(BaseDir, "backup");

    public string StatePathFor(string gameRoot) =>
        Path.Combine(StateDir, KeyFor(gameRoot) + ".json");

    public string BackupDirFor(string gameRoot) =>
        Path.Combine(BackupRoot, KeyFor(gameRoot));

    private static string KeyFor(string gameRoot)
    {
        var norm = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(norm))[..16];
    }

    public InstallState? TryLoad(string gameRoot)
    {
        var p = StatePathFor(gameRoot);
        if (!File.Exists(p)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(p), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public void Save(InstallState state)
    {
        Directory.CreateDirectory(StateDir);
        var p = StatePathFor(state.GameRoot);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
        File.Move(tmp, p, overwrite: true);
    }

    public void Delete(string gameRoot)
    {
        var p = StatePathFor(gameRoot);
        if (File.Exists(p)) File.Delete(p);
    }

    // ------------------------------------------------------------ 第三方组件记录

    public string ComponentsPathFor(string gameRoot) =>
        Path.Combine(StateDir, KeyFor(gameRoot) + ".components.json");

    public ComponentState? TryLoadComponents(string gameRoot)
    {
        var p = ComponentsPathFor(gameRoot);
        if (!File.Exists(p)) return null;
        try
        {
            return JsonSerializer.Deserialize<ComponentState>(File.ReadAllText(p), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public void SaveComponents(ComponentState state)
    {
        Directory.CreateDirectory(StateDir);
        var p = ComponentsPathFor(state.GameRoot);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
        File.Move(tmp, p, overwrite: true);
    }

    public void DeleteComponents(string gameRoot)
    {
        var p = ComponentsPathFor(gameRoot);
        if (File.Exists(p)) File.Delete(p);
    }
}
