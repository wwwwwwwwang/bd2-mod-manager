using BD2ModManager.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var store = new StateStore();
var installer = new LoaderInstaller(store);

if (args.Length == 0) { Help(); Pause(); return 0; }

try
{
    return args[0].ToLowerInvariant() switch
    {
        "find" or "scan" => CmdFind(),
        "status" => CmdStatus(args),
        "install" => CmdInstall(args),
        "uninstall" => CmdUninstall(args),
        "clean-residue" or "cleanresidue" => CmdCleanResidue(args),
        "plugins" => CmdPlugins(args),
        "enable" => CmdToggle(args, true),
        "disable" => CmdToggle(args, false),
        "components" => CmdComponents(args),
        "component-install" => CmdComponentInstall(args),
        "component-remove" => CmdComponentRemove(args),
        "component-enable" => CmdComponentToggle(args, true),
        "component-disable" => CmdComponentToggle(args, false),
        "help" or "-h" or "--help" => Help(),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"错误：{ex.Message}");
    return 2;
}

// ------------------------------------------------------------------ 命令

int CmdFind()
{
    Console.WriteLine("正在扫描 BD2 PC 客户端…");
    var games = new GameLocator().Find();

    if (games.Count == 0)
    {
        Console.WriteLine("未找到。可能未安装，或安装在非标准路径。");
        Console.WriteLine("可以手动指定：bd2mm status \"<游戏目录>\"");
        return 1;
    }

    foreach (var g in games)
    {
        Console.WriteLine();
        Console.WriteLine($"  目录      {g.Root}");
        Console.WriteLine($"  主程序    {g.ExeName}");
        Console.WriteLine($"  数据目录  {Path.GetFileName(g.DataDir)}");
        Console.WriteLine($"  Unity     {g.UnityVersion ?? "(未识别)"}");
        Console.WriteLine($"  来源      {g.Source}");
    }
    return 0;
}

int CmdStatus(string[] a)
{
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var st = LoaderInspector.Inspect(game, store);

    Console.WriteLine($"游戏目录  {game.Root}");
    Console.WriteLine($"Unity     {game.UnityVersion ?? "(未识别)"}");
    Console.WriteLine();

    if (!st.Present)
    {
        Console.WriteLine("Loader    未安装");
        Console.WriteLine("          游戏目录干净，可以安全安装。");
    }
    else if (st.ResidueOnly)
    {
        Console.WriteLine("Loader    仅剩残留");
        Console.WriteLine("          loader 核心已不在（缺 winhttp.dll 与 BepInEx/core），游戏不会加载任何插件。");
        Console.WriteLine("          缓存与日志可用 GUI 的「清理残留」一键清掉。");
    }
    else
    {
        Console.WriteLine($"Loader    已安装");
        Console.WriteLine($"  BepInEx    {st.BepInExVersion ?? "(未知)"}");
        Console.WriteLine($"  Doorstop   {st.DoorstopVersion ?? "(未知)"}");
        Console.WriteLine($"  安装来源   {(st.ManagedByUs ? $"本工具（{st.DetectedPackageVersion}）" : "其他人 / 手工安装")}");
    }

    if (st.Warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("警告：");
        foreach (var w in st.Warnings) Console.WriteLine($"  ! {w}");
    }

    Console.WriteLine();
    if (st.Plugins.Count == 0)
    {
        Console.WriteLine("插件      无");
    }
    else
    {
        Console.WriteLine($"插件      {st.Plugins.Count} 个");
        foreach (var p in st.Plugins)
        {
            var state = p.Enabled ? "启用" : "禁用";
            Console.WriteLine($"  [{state}] {p.Name,-44} {p.Size / 1024.0,8:F1} KB");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"游戏进程   {(ProcessGuard.IsGameRunning() ? "运行中（操作前请退出）" : "未运行")}");
    return 0;
}

int CmdInstall(string[] a)
{
    if (a.Length < 2) { Console.WriteLine("用法：bd2mm install <游戏目录> --package <包目录> [--force]"); return 1; }

    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var pkg = ArgValue(a, "--package");
    if (pkg == null)
    {
        Console.WriteLine("缺少 --package <包目录或 zip>");
        return 1;
    }

    // 目录与 zip 都支持；zip 会解到临时目录，方法结束时清理
    using var pkgHandle = LoaderPackage.OpenPath(pkg);
    if (!pkgHandle.IsValid)
    {
        Console.WriteLine(pkgHandle.Problem ?? "包不可用。");
        return 1;
    }

    bool force = a.Contains("--force");

    Console.WriteLine($"目标   {game.Root}");
    Console.WriteLine($"包     {Path.GetFullPath(pkg)}（loader {pkgHandle.Version ?? "未知"}）");
    Console.WriteLine();

    var r = installer.Install(game, pkgHandle.Directory, force);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdUninstall(string[] a)
{
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    Console.WriteLine($"目标   {game.Root}");
    Console.WriteLine();

    var r = installer.Uninstall(game);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdCleanResidue(string[] a)
{
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    Console.WriteLine($"目标   {game.Root}");
    Console.WriteLine();

    var r = installer.CleanResidue(game);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdPlugins(string[] a)
{
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var st = LoaderInspector.Inspect(game, store);
    if (st.Plugins.Count == 0) { Console.WriteLine("没有插件。"); return 0; }

    foreach (var p in st.Plugins)
        Console.WriteLine($"[{(p.Enabled ? "启用" : "禁用")}] {p.DllPath}");
    return 0;
}

int CmdToggle(string[] a, bool enable)
{
    if (a.Length < 3) { Console.WriteLine($"用法：bd2mm {(enable ? "enable" : "disable")} <游戏目录> <dll 路径>"); return 1; }
    var game = ResolveGame(a, 1);
    if (game == null) return 1;
    var r = installer.SetPluginEnabled(game.Root, a[2], enable);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdComponents(string[] a)
{
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var comps = new ComponentManager(store).List(game);
    if (comps.Count == 0)
    {
        Console.WriteLine("没有由本工具安装的组件。");
        Console.WriteLine("安装：bd2mm component-install <游戏目录> <插件目录或 zip>");
        return 0;
    }

    foreach (var c in comps)
    {
        var state = c.Missing ? "缺失" : c.Enabled ? "启用" : "禁用";
        Console.WriteLine($"[{state}] {c.Name}  {c.FileCount} 个文件 · {c.Size / 1024.0:F0} KB");
        Console.WriteLine($"         {c.RelativeDir}  ← {c.Source}");
    }
    return 0;
}

int CmdComponentInstall(string[] a)
{
    if (a.Length < 3)
    {
        Console.WriteLine("用法：bd2mm component-install <游戏目录> <插件目录或 zip>");
        return 1;
    }
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var r = new ComponentManager(store).Install(game, a[2]);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdComponentRemove(string[] a)
{
    if (a.Length < 3)
    {
        Console.WriteLine("用法：bd2mm component-remove <游戏目录> <组件名>");
        return 1;
    }
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var r = new ComponentManager(store).Remove(game, a[2]);
    Print(r);
    return r.Success ? 0 : 1;
}

int CmdComponentToggle(string[] a, bool enable)
{
    if (a.Length < 3)
    {
        Console.WriteLine($"用法：bd2mm component-{(enable ? "enable" : "disable")} <游戏目录> <组件名>");
        return 1;
    }
    var game = ResolveGame(a, 1);
    if (game == null) return 1;

    var r = new ComponentManager(store).SetEnabled(game, a[2], enable);
    Print(r);
    return r.Success ? 0 : 1;
}

// ------------------------------------------------------------------ 辅助
GameInstall? ResolveGame(string[] a, int index)
{
    if (a.Length > index && !a[index].StartsWith("--"))
    {
        var g = GameLocator.TryBuild(a[index], "指定路径");
        if (g == null)
        {
            Console.WriteLine($"该目录不是有效的 BD2 安装：{a[index]}");
            Console.WriteLine("需要同时存在 \"BrownDust II.exe\" 和 \"BrownDust II_Data\"。");
        }
        return g;
    }

    var games = new GameLocator().Find();
    if (games.Count == 1) return games[0];
    if (games.Count == 0) { Console.WriteLine("未找到游戏，请手动指定目录。"); return null; }

    Console.WriteLine("找到多个安装，请指定其一：");
    foreach (var g in games) Console.WriteLine($"  {g.Root}");
    return null;
}

static string? ArgValue(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    return null;
}

static void Print(OpResult r)
{
    foreach (var l in r.Log) Console.WriteLine($"  {l}");
    foreach (var e in r.Errors) Console.Error.WriteLine($"  ✗ {e}");
    Console.WriteLine();
    Console.WriteLine(r.Success ? "结果：成功" : "结果：失败");
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"未知命令：{cmd}");
    Help();
    return 1;
}

static void Pause()
{
    // 双击 exe 时控制台会随进程退出立刻关闭，像「一闪而过」。这里停一下。
    if (!Environment.UserInteractive || Console.IsInputRedirected) return;
    Console.WriteLine();
    Console.WriteLine("按任意键退出…");
    try { Console.ReadKey(intercept: true); } catch { }
}

static int Help()
{
    Console.WriteLine("""
BD2ModManager — BD2 的 BepInEx 加载器管理工具（命令行版）

  bd2mm find                              扫描并列出所有 BD2 安装
  bd2mm status  [游戏目录]                查看 loader 与插件状态
  bd2mm install <游戏目录> --package <包目录或 zip> [--force]
                                          安装 loader
  bd2mm uninstall <游戏目录>              卸载并还原
  bd2mm clean-residue <游戏目录>          清掉 loader 残留（缓存与日志，不动插件）
  bd2mm plugins <游戏目录>                列出插件
  bd2mm enable  <游戏目录> <dll>          启用插件
  bd2mm disable <游戏目录> <dll>          禁用插件

  bd2mm components <游戏目录>                     列出第三方组件
  bd2mm component-install  <游戏目录> <目录或 zip>  安装组件
  bd2mm component-remove   <游戏目录> <组件名>      移除组件（按记录校验哈希）
  bd2mm component-enable   <游戏目录> <组件名>      启用组件
  bd2mm component-disable  <游戏目录> <组件名>      禁用组件

不指定游戏目录时自动探测。

日常使用请双击 BD2ModManager.exe（图形界面版），无需命令行。
""");
    return 0;
}
