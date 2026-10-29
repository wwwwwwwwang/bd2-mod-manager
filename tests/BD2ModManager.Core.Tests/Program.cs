using BD2ModManager.Core;

namespace BD2ModManager.Core.Tests;

/// <summary>
/// 三个回归场景：
///  1. 旧版卸载留下的残留（core 已删、只剩 cache/config/log）必须被认成「残留」而不是「他人安装」；
///  2. 残留清理只删缓存日志，plugins 里的插件原样保留；
///  3. 完整「安装 → 运行 → 卸载」之后，游戏目录必须回到安装前的干净状态。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static string _workRoot = "";

    private static int Main(string[] args)
    {
        Console.WriteLine("BD2ModManager.Core 离线验证");
        Console.WriteLine($"临时工作目录：{_workRoot = NewDir("bd2mm-core-test")}");
        Console.WriteLine();

        try
        {
            IncompleteLoaderDetection();
            ResidueDetection();
            ResidueCleanup();
            TogglePathContract();
            ComponentPathDrift();
            FullInstallUninstallCycle(args.Length > 0 ? args[0] : null);
        }
        finally
        {
            TryDeleteDir(_workRoot);
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} 项，失败 {_fail} 项。");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 场景 0

    /// <summary>
    /// 真实故障：只有 winhttp.dll，BepInEx/core 缺失。
    /// 这不能被当成“已有可用 BepInEx”，否则只会装插件，插件永远不会加载。
    /// </summary>
    private static void IncompleteLoaderDetection()
    {
        Console.WriteLine("[0] 不完整 loader：只有 winhttp.dll");
        var root = NewGameDir("incomplete");
        var store = NewStore();
        var game = Game(root);

        Directory.CreateDirectory(Path.Combine(root, "BepInEx"));
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "dummy");

        var st = LoaderInspector.Inspect(game, store);

        Check("不完整 loader 不算 CorePresent", !st.CorePresent);
        Check("标记为 IncompleteLoader", st.IncompleteLoader);
        Check("不被当成普通残留", !st.ResidueOnly);
        Check("警告包含 core 缺失",
            st.Warnings.Any(w => w.Contains("BepInEx/core", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", st.Warnings));

        var src = NewDir("IncompleteComp");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "IncompleteComp.dll"), "dll");

        var install = new ComponentManager(store).Install(game, src);
        Check("不完整 loader 下拒绝安装组件", !install.Success,
            string.Join(" | ", install.Errors));

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 场景 1

    private static void ResidueDetection()
    {
        Console.WriteLine("[1] 残留识别：core 已删、只剩缓存日志");
        var root = NewGameDir("residue");
        var store = NewStore();
        var game = Game(root);

        SeedResidue(root);

        var st = LoaderInspector.Inspect(game, store);

        Check("Present = true（目录里确实有痕迹）", st.Present);
        Check("CorePresent = false（没有 winhttp.dll 也没有 core）", !st.CorePresent);
        Check("ResidueOnly = true", st.ResidueOnly);
        Check("不再冒出「缺少 winhttp.dll」的误导提醒",
            !st.Warnings.Any(w => w.Contains("缺少 winhttp.dll")),
            string.Join(" | ", st.Warnings));
        Check("给出的是残留提示",
            st.Warnings.Any(w => w.Contains("残留")),
            string.Join(" | ", st.Warnings));
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 场景 2

    private static void ResidueCleanup()
    {
        Console.WriteLine("[2] 残留清理：只删缓存与日志，插件保留");
        var root = NewGameDir("cleanup");
        var store = NewStore();
        var game = Game(root);

        SeedResidue(root);
        var vendor = Path.Combine(root, "BepInEx", "plugins", "vendor.dll.disabled");
        Directory.CreateDirectory(Path.GetDirectoryName(vendor)!);
        File.WriteAllText(vendor, "not ours");

        var installer = new LoaderInstaller(store);
        var r = installer.CleanResidue(game);

        Check("清理成功", r.Success, string.Join(" | ", r.Errors));
        Check("cache 目录已删除", !Directory.Exists(Path.Combine(root, "BepInEx", "cache")));
        Check("LogOutput.log 已删除", !File.Exists(Path.Combine(root, "BepInEx", "LogOutput.log")));
        Check("BepInEx.cfg 已删除", !File.Exists(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg")));
        Check("第三方插件原样保留", File.Exists(vendor));

        // 插件还在，说明工具不越权删别人的东西
        var after = LoaderInspector.Inspect(game, store);
        Check("清完不再算「残留」（缓存日志都没了）", !after.ResidueOnly);
        Check("可清理痕迹归零", !after.HasTraces);
        Check("残留里能列出那个插件", after.Plugins.Any(p => p.Name.StartsWith("vendor")));
        Check("只剩插件也不算他人安装", !after.ManagedByUs && !after.CorePresent);

        // 用户自己把插件删掉之后，再清一次应该连目录一起消失
        File.Delete(vendor);
        var r2 = installer.CleanResidue(game);
        Check("第二次清理成功", r2.Success, string.Join(" | ", r2.Errors));
        Check("BepInEx 目录整个消失", !Directory.Exists(Path.Combine(root, "BepInEx")));

        var gone = LoaderInspector.Inspect(game, store);
        Check("状态回到「未启用」", !gone.Present && !gone.ResidueOnly);
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 场景 3

    /// <summary>
    /// 开关是改文件名：.dll ↔ .dll.disabled。
    /// 改名之后原来那条路径就作废了，调用方必须重新枚举，
    /// 否则下一次切换会拿旧路径去判断，报「该插件当前已是禁用/启用状态」。
    /// 0.4.1 界面上那串反复「失败」就是这么来的。
    /// </summary>
    private static void TogglePathContract()
    {
        Console.WriteLine("[3] 开关改名后旧路径必须失效");
        var root = NewGameDir("toggle");
        var store = NewStore();
        var game = Game(root);

        var plugin = Path.Combine(root, "BepInEx", "plugins", "Demo.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        File.WriteAllText(plugin, "dll");

        var installer = new LoaderInstaller(store);
        var pluginsDir = Path.Combine(root, "BepInEx", "plugins");

        var first = LoaderInspector.ListPlugins(Path.Combine(root, "BepInEx"));
        Check("初始读到 1 个启用插件", first.Count == 1 && first[0].Enabled);
        var stalePath = first[0].DllPath;

        var off = installer.SetPluginEnabled(root, stalePath, false);
        Check("禁用成功", off.Success, string.Join(" | ", off.Errors));
        Check("文件已改名为 .disabled", File.Exists(plugin + ".disabled"));

        var second = LoaderInspector.ListPlugins(Path.Combine(root, "BepInEx"));
        Check("重新枚举得到禁用状态", second.Count == 1 && !second[0].Enabled);
        Check("新路径指向 .disabled", second[0].DllPath.EndsWith(".disabled"));
        Check("路径确实变了（这就是界面必须重新读的原因）", second[0].DllPath != stalePath);

        var again = installer.SetPluginEnabled(root, stalePath, false);
        Check("拿旧路径再禁用 → 报「已是禁用状态」", !again.Success,
            string.Join(" | ", again.Errors));

        var on = installer.SetPluginEnabled(root, second[0].DllPath, true);
        Check("用新路径启用成功", on.Success, string.Join(" | ", on.Errors));
        Check("文件回到 .dll", File.Exists(plugin) && !File.Exists(plugin + ".disabled"));
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 场景 4

    /// <summary>
    /// 组件开关同样是改文件名，而记录里存的是「切换那一刻的路径」。
    /// 一旦记录停在另一侧（外部改过名、或上次切换中断），按它找文件就会落空：
    /// 界面上显示 0 KB，移除组件时又静默跳过 —— 文件留在磁盘上变成「外来插件」。
    /// </summary>
    private static void ComponentPathDrift()
    {
        Console.WriteLine("[4] 组件记录路径漂移：仍要算得出大小、删得干净");
        var root = NewGameDir("compdrift");
        var store = NewStore();
        var game = Game(root);
        var comp = new ComponentManager(store);

        // 模拟完整的精简版 loader：有入口、配置和核心，但没有预置 plugins 目录
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), "dummy");
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "dummy");
        File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "");

        var src = NewDir("CompDemo");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "CompDemo.dll"), new byte[4096]);

        var inst = comp.Install(game, src);
        Check("组件安装成功", inst.Success, string.Join(" | ", inst.Errors));
        Check("空 plugins 目录由组件安装自动创建",
            Directory.Exists(Path.Combine(root, "BepInEx", "plugins")));

        var first = comp.List(game);
        Check("读到 1 个组件", first.Count == 1, $"count={first.Count}");
        if (first.Count != 1) { Console.WriteLine(); return; }
        Check("初始大小不为 0", first[0].Size > 0, $"size={first[0].Size}");

        var name = first[0].Name;
        var dir = Path.Combine(root, first[0].RelativeDir.Replace('/', Path.DirectorySeparatorChar));

        var off = comp.SetEnabled(game, name, false);
        Check("禁用成功", off.Success, string.Join(" | ", off.Errors));

        var disabledPath = Path.Combine(dir, "CompDemo.dll.disabled");
        var enabledPath = Path.Combine(dir, "CompDemo.dll");
        Check("文件已改名为 .disabled", File.Exists(disabledPath));

        // 模拟外部把文件改回启用态：记录仍停在 .disabled
        File.Move(disabledPath, enabledPath);

        var drifted = comp.List(game);
        Check("记录漂移时仍算得出大小（不是 0 KB）",
            drifted.Count == 1 && drifted[0].Size > 0, $"size={drifted[0].Size}");
        Check("记录漂移时不误报「文件已不在磁盘上」",
            drifted.Count == 1 && !drifted[0].Missing);

        var on = comp.SetEnabled(game, name, true);
        Check("重新启用成功", on.Success, string.Join(" | ", on.Errors));
        Check("文件仍是 .dll", File.Exists(enabledPath) && !File.Exists(disabledPath));

        var healed = comp.List(game);
        Check("矫正后大小正常", healed.Count == 1 && healed[0].Size > 0, $"size={healed[0].Size}");

        var removed = comp.Remove(game, name);
        Check("移除成功", removed.Success, string.Join(" | ", removed.Errors));
        Check("dll 真的被删掉了（不留残留）", !File.Exists(enabledPath) && !File.Exists(disabledPath));
        Check("组件记录已清空", comp.List(game).Count == 0);
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 场景 5

    private static void FullInstallUninstallCycle(string? packagePath)
    {
        Console.WriteLine("[4] 完整流程：安装 → 模拟运行产生缓存 → 卸载");
        var zip = packagePath ?? FindPackageZip();
        if (zip == null)
        {
            Check("找到 loader 包", false, "dist/ 里没有 bd2-loader*.zip，可用第一个参数指定");
            Console.WriteLine();
            return;
        }
        Check("找到 loader 包", true);

        var root = NewGameDir("cycle");
        var store = NewStore();
        var game = Game(root);
        var installer = new LoaderInstaller(store);

        using var pkg = LoaderPackage.OpenPath(zip);
        Check("包可用", pkg.IsValid, pkg.Problem);
        if (!pkg.IsValid) { Console.WriteLine(); return; }

        var before = Directory.GetFileSystemEntries(root).Length;

        var install = installer.Install(game, pkg.Directory);
        Check("安装成功", install.Success, string.Join(" | ", install.Errors));
        Check("winhttp.dll 已就位", File.Exists(Path.Combine(root, "winhttp.dll")));
        Check("BepInEx/core 已就位", Directory.Exists(Path.Combine(root, "BepInEx", "core")));

        var live = LoaderInspector.Inspect(game, store);
        Check("装完是「已启用」", live.Present && live.CorePresent && live.ManagedByUs);

        // 模拟 loader 跑过一次：生成缓存、日志、自带配置
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "cache"));
        File.WriteAllText(Path.Combine(root, "BepInEx", "cache", "chainloader.dat"), "cache");
        File.WriteAllText(Path.Combine(root, "BepInEx", "LogOutput.log"), "[Info] loaded");
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg"), "[Logging]");

        var uninstall = installer.Uninstall(game);
        Check("卸载成功", uninstall.Success, string.Join(" | ", uninstall.Errors));
        Check("卸载日志里没有「仍有残留」",
            !uninstall.Log.Any(l => l.Contains("仍有 BepInEx") && !l.Contains("非本工具")),
            string.Join(" | ", uninstall.Log));
        Check("缓存已清理", !Directory.Exists(Path.Combine(root, "BepInEx", "cache")));

        var end = LoaderInspector.Inspect(game, store);
        Check("卸载后不再是「他人安装」", !end.Present && !end.ResidueOnly);
        Check("游戏目录回到安装前的条目数",
            Directory.GetFileSystemEntries(root).Length == before,
            $"现在 {Directory.GetFileSystemEntries(root).Length}，原为 {before}");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 造数据

    /// <summary>复刻旧版卸载后的现场：core 被删，缓存日志留着。</summary>
    private static void SeedResidue(string root)
    {
        var bep = Path.Combine(root, "BepInEx");
        Directory.CreateDirectory(Path.Combine(bep, "cache"));
        File.WriteAllText(Path.Combine(bep, "cache", "chainloader_typeloader.dat"), "x");

        Directory.CreateDirectory(Path.Combine(bep, "config"));
        File.WriteAllText(Path.Combine(bep, "config", "BepInEx.cfg"), "[Logging]");
        File.WriteAllText(Path.Combine(bep, "config", "com.bepis.bepinex.configurationmanager.cfg"), "[x]");

        Directory.CreateDirectory(Path.Combine(bep, "plugins"));
        File.WriteAllText(Path.Combine(bep, "LogOutput.log"), "[Info] loaded");
    }

    private static GameInstall Game(string root) => new(root, Path.Combine(root, "Game.exe"), "", null, "test");

    private static StateStore NewStore() => new(Path.Combine(_workRoot, "state-" + Guid.NewGuid().ToString("n")[..6]));

    private static string NewGameDir(string name)
    {
        var d = Path.Combine(_workRoot, "game-" + name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string NewDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string? FindPackageZip()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var dist = Path.Combine(dir.FullName, "dist");
            if (Directory.Exists(dist))
            {
                // 本地是 bd2-loader-0.4.1.zip，CI 生成的是 bd2-loader.zip，两种都要认
                var zip = Directory.EnumerateFiles(dist, "bd2-loader*.zip")
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                   .LastOrDefault();
                if (zip != null) return zip;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static void TryDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}" + (string.IsNullOrWhiteSpace(detail) ? "" : $"  →  {detail}"));
        }
    }
}
