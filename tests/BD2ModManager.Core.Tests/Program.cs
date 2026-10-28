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
            ResidueDetection();
            ResidueCleanup();
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
        Check("清完仍是残留态（因为插件还在）", after.ResidueOnly);
        Check("残留里能列出那个插件", after.Plugins.Any(p => p.Name.StartsWith("vendor")));

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

    private static void FullInstallUninstallCycle(string? packagePath)
    {
        Console.WriteLine("[3] 完整流程：安装 → 模拟运行产生缓存 → 卸载");
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
