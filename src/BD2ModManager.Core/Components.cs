using System.IO.Compression;

namespace BD2ModManager.Core;

/// <summary>一个由本工具安装的第三方组件（一个插件目录或单个 dll）。</summary>
public sealed record ComponentInfo(
    string Name,
    string RelativeDir,
    int FileCount,
    long Size,
    bool Enabled,
    string Source,
    DateTimeOffset InstalledAt,
    bool Missing);

/// <summary>组件安装记录。只记录本工具写进去的文件，移除时逐项校验哈希。</summary>
public sealed class ComponentRecord
{
    public string Name { get; set; } = "";
    /// <summary>相对游戏目录，例如 BepInEx/plugins/第三方闭源插件。</summary>
    public string RelativeDir { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;
    public bool Enabled { get; set; } = true;
    public List<StateFile> Files { get; set; } = new();
    public List<string> Dirs { get; set; } = new();
}

public sealed class ComponentState
{
    public int Schema { get; set; } = 1;
    public string GameRoot { get; set; } = "";
    public List<ComponentRecord> Components { get; set; } = new();
}

/// <summary>
/// 第三方组件管理。约定与 loader 安装保持一致：
///  · 只安装到 BepInEx/plugins 下，一个组件一个子目录；
///  · 每个文件记录 sha256，移除时只删「仍是自己写的那份」；
///  · 目录里原来就有的同名插件会被纳入记录（不复制第二份，避免 BepInEx 重复加载）。
/// </summary>
public sealed class ComponentManager
{
    private const string PluginsRel = "BepInEx/plugins";

    private readonly StateStore _store;

    public ComponentManager(StateStore store) => _store = store;

    // ---------------------------------------------------------------- 查询

    public List<ComponentInfo> List(GameInstall game)
    {
        var state = _store.TryLoadComponents(game.Root);
        if (state == null) return new List<ComponentInfo>();

        var list = new List<ComponentInfo>();
        foreach (var c in state.Components)
        {
            var abs = Path.Combine(game.Root, c.RelativeDir.Replace('/', Path.DirectorySeparatorChar));
            long size = 0;
            foreach (var f in c.Files)
            {
                try
                {
                    var p = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(p)) size += new FileInfo(p).Length;
                }
                catch { }
            }

            list.Add(new ComponentInfo(
                c.Name, c.RelativeDir, c.Files.Count, size, c.Enabled, c.Source,
                c.InstalledAt, !Directory.Exists(abs) && !File.Exists(abs)));
        }
        return list;
    }

    /// <summary>该路径是否落在某个组件的管理范围内（用于把散装插件和组件区分开）。</summary>
    public static bool IsInside(string pluginsDir, string dllPath, IEnumerable<ComponentInfo> components)
    {
        foreach (var c in components)
        {
            var root = Path.Combine(pluginsDir, c.Name);
            if (string.Equals(Path.GetFullPath(dllPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return true;
            if (dllPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 安装

    public OpResult Install(GameInstall game, string sourcePath)
    {
        var pluginsAbs = Path.Combine(game.Root, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsAbs))
            return OpResult.Fail("还没有启用 Mod 支持（找不到 BepInEx\\plugins）。先打开上面的开关再装组件。");

        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先完全退出游戏再安装组件。");

        string? temp = null;
        try
        {
            string payload;
            string source;

            if (Directory.Exists(sourcePath))
            {
                payload = sourcePath;
                source = "文件夹：" + sourcePath;
            }
            else if (File.Exists(sourcePath) &&
                     sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                temp = Path.Combine(Path.GetTempPath(), "bd2mm-cmp-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(temp);
                ZipFile.ExtractToDirectory(sourcePath, temp, overwriteFiles: true);
                payload = temp;
                source = "压缩包：" + Path.GetFileName(sourcePath);
            }
            else
            {
                return OpResult.Fail($"不支持的来源：{sourcePath}", "需要一个插件文件夹或 .zip（7z 请先解压）。");
            }

            var roots = ResolveEntries(payload);
            if (roots.Count == 0)
                return OpResult.Fail("在这个位置没有找到插件（.dll）。",
                    "请选中解压后的插件目录；如果里面是 BepInEx\\plugins\\…，也可以直接选它的上一层。");

            var state = _store.TryLoadComponents(game.Root) ?? new ComponentState { GameRoot = game.Root };
            var r = new OpResult();
            int ok = 0;
            int before = state.Components.Count;

            foreach (var (name, rootDir, files) in roots)
            {
                var one = InstallOne(game, state, name, rootDir, files, source);
                foreach (var l in one.Log) r.Info(l);
                foreach (var e in one.Errors) r.Error(e);
                if (one.Success) ok++;
            }

            if (ok == 0)
            {
                r.Success = false;
                if (r.Errors.Count == 0) r.Error("没有安装任何组件。");
                return r;
            }

            _store.SaveComponents(state);
            r.Success = true;
            var added = state.Components.Count - before;
            r.Info($"已安装 {added} 个组件。重启游戏后生效。");
            return r;
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"安装组件失败：{ex.Message}");
        }
        finally
        {
            if (temp != null)
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }
    }

    /// <summary>把 payload 归一化成若干「组件名 + 根目录 + 文件列表」。</summary>
    private static List<(string Name, string RootDir, List<string> Files)> ResolveEntries(string payload)
    {
        // 允许直接选 BepInEx 的上一层，或 plugins 那一层
        var entriesRoot = payload;
        var nested = Path.Combine(payload, "BepInEx", "plugins");
        if (Directory.Exists(nested)) entriesRoot = nested;

        var result = new List<(string, string, List<string>)>();

        foreach (var entry in Directory.GetFileSystemEntries(entriesRoot))
        {
            if (Directory.Exists(entry))
            {
                var files = Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).ToList();
                if (files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)))
                    result.Add((Path.GetFileName(entry), entry, files));
                continue;
            }

            var fileName = Path.GetFileName(entry);
            var isDll = fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var isDisabled = fileName.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);
            if (!isDll && !isDisabled) continue;   // winhttp.dll 之类不归组件管

            var name = isDisabled ? fileName[..^".disabled".Length] : fileName;
            name = Path.GetFileNameWithoutExtension(name);
            result.Add((name, Path.GetDirectoryName(entry)!, new List<string> { entry }));
        }

        return result;
    }

    private OpResult InstallOne(GameInstall game, ComponentState state, string name, string srcRoot,
        List<string> srcFiles, string source)
    {
        var r = new OpResult();
        var relDir = $"{PluginsRel}/{name}";
        var absDir = Path.Combine(game.Root, relDir.Replace('/', Path.DirectorySeparatorChar));

        // loader 自带的插件（例如 ConfigurationManager）不归组件管，避免抢走它的文件
        var loaderState = _store.TryLoad(game.Root);
        if (loaderState != null)
        {
            var prefix = relDir + "/";
            bool owned = loaderState.Added.Any(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      || loaderState.Replaced.Any(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (owned)
            {
                r.Success = true;
                r.Info($"组件 {name} 由加载器自带，跳过。");
                return r;
            }
        }

        // 目标位置已经有完全相同的一份（例如 loader 自带的 ConfigurationManager）：不重复接管
        bool allSame = srcFiles.Count > 0 && srcFiles.All(src =>
        {
            var dst = Path.Combine(absDir, Path.GetRelativePath(srcRoot, src));
            return File.Exists(dst) &&
                   string.Equals(Hashing.Sha256File(dst), Hashing.Sha256File(src), StringComparison.OrdinalIgnoreCase);
        });
        if (allSame)
        {
            r.Success = true;
            r.Info($"组件 {name} 已存在且内容相同，未重复安装。");
            return r;
        }

        // 组件名撞车：先看是不是同一个组件重装
        var old = state.Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (old != null)
        {
            var rm = RemoveInternal(game, state, old);
            foreach (var l in rm.Log) r.Info("（重装）" + l);
        }

        var dirs = new List<string>();
        var files = new List<StateFile>();

        try
        {
            EnsureDir(game.Root, relDir, dirs);

            foreach (var src in srcFiles)
            {
                var sub = Path.GetRelativePath(srcRoot, src);
                var rel = $"{relDir}/{sub.Replace('\\', '/')}";
                var dst = Path.Combine(absDir, sub);
                var subDir = Path.GetDirectoryName(sub);
                if (!string.IsNullOrEmpty(subDir)) EnsureDir(game.Root, relDir + "/" + subDir.Replace('\\', '/'), dirs);

                // 只有根级的单个 dll 才做「散装同名插件」合并，避免 BepInEx 重复加载
                if (string.IsNullOrEmpty(subDir))
                {
                    var fileName = Path.GetFileName(src);
                    var loose = Path.Combine(game.Root, PluginsRel, fileName);
                    if (File.Exists(loose) && !string.Equals(Path.GetFullPath(loose), Path.GetFullPath(dst),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var looseHash = Hashing.Sha256File(loose);
                        var srcHash = Hashing.Sha256File(src);
                        if (string.Equals(looseHash, srcHash, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(loose);
                            r.Info($"把已有的同名插件纳入组件：{fileName}");
                        }
                        else
                        {
                            r.Error($"已存在同名插件且内容不同，已跳过：{loose}");
                            return r;
                        }
                    }
                }

                var hash = Hashing.Sha256File(src);
                CopyAtomic(src, dst);
                files.Add(new StateFile { Path = rel, Sha256 = hash });
            }

            var rec = new ComponentRecord
            {
                Name = name,
                RelativeDir = relDir,
                Source = source,
                InstalledAt = DateTimeOffset.Now,
                Enabled = files.Any(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                Files = files,
                Dirs = dirs
            };
            state.Components.Add(rec);
            state.GameRoot = game.Root;

            r.Success = true;
            r.Info($"安装组件 {name}：{files.Count} 个文件");
            return r;
        }
        catch (Exception ex)
        {
            r.Error($"安装组件 {name} 失败：{ex.Message}");
            return r;
        }
    }

    // ---------------------------------------------------------------- 移除

    public OpResult Remove(GameInstall game, string name)
    {
        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先完全退出游戏再移除组件。");

        var state = _store.TryLoadComponents(game.Root);
        var rec = state?.Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state == null || rec == null)
            return OpResult.Fail($"没有这个组件的安装记录：{name}");

        var r = RemoveInternal(game, state, rec);
        if (!r.Success) return r;

        state.Components.Remove(rec);
        if (state.Components.Count == 0) _store.DeleteComponents(game.Root);
        else _store.SaveComponents(state);

        r.Success = true;
        return r;
    }

    public OpResult RemoveAll(GameInstall game)
    {
        var state = _store.TryLoadComponents(game.Root);
        if (state == null || state.Components.Count == 0) { var ok = OpResult.Ok(); return ok; }

        var r = new OpResult();
        foreach (var c in state.Components.ToList())
        {
            var one = RemoveInternal(game, state, c);
            foreach (var l in one.Log) r.Info(l);
            foreach (var e in one.Errors) r.Error(e);
        }
        _store.DeleteComponents(game.Root);
        r.Success = r.Errors.Count == 0;
        return r;
    }

    private OpResult RemoveInternal(GameInstall game, ComponentState state, ComponentRecord rec)
    {
        var r = new OpResult();
        int kept = 0;

        foreach (var f in rec.Files)
        {
            var abs = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs)) continue;
            try
            {
                var cur = Hashing.Sha256File(abs);
                if (!string.Equals(cur, f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    r.Info($"保留（已被修改）：{f.Path}");
                    kept++;
                    continue;
                }
                File.Delete(abs);
            }
            catch (Exception ex)
            {
                r.Error($"删除失败 {f.Path}：{ex.Message}");
                kept++;
            }
        }

        foreach (var d in rec.Dirs.OrderByDescending(x => x.Length))
        {
            var dir = Path.Combine(game.Root, d.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }

        r.Success = true;
        if (kept == 0) r.Info($"已移除组件 {rec.Name}");
        else r.Info($"移除组件 {rec.Name}：{kept} 个文件被保留");
        state.Components.Remove(rec);
        return r;
    }

    // ---------------------------------------------------------------- 开关

    public OpResult SetEnabled(GameInstall game, string name, bool enabled)
    {
        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先退出再切换组件。");

        var state = _store.TryLoadComponents(game.Root);
        var rec = state?.Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state == null || rec == null) return OpResult.Fail($"没有这个组件的安装记录：{name}");

        var r = new OpResult();
        int changed = 0;

        foreach (var f in rec.Files.ToList())
        {
            var abs = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            var isDll = abs.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var isOff = abs.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);
            if (!isDll && !isOff) continue;

            try
            {
                if (enabled && isOff)
                {
                    var target = abs[..^".disabled".Length];
                    if (File.Exists(target)) continue;
                    if (!File.Exists(abs)) { f.Path = ToRel(game.Root, target); continue; }
                    File.Move(abs, target);
                    f.Path = ToRel(game.Root, target);
                    changed++;
                }
                else if (!enabled && isDll)
                {
                    if (!File.Exists(abs)) continue;
                    File.Move(abs, abs + ".disabled");
                    f.Path = ToRel(game.Root, abs + ".disabled");
                    changed++;
                }
            }
            catch (Exception ex)
            {
                r.Error($"切换失败 {Path.GetFileName(abs)}：{ex.Message}");
            }
        }

        rec.Enabled = enabled;
        _store.SaveComponents(state);

        r.Success = r.Errors.Count == 0;
        r.Info(changed == 0
            ? $"组件 {name} 已是{(enabled ? "启用" : "禁用")}状态。"
            : $"组件 {name} 已{(enabled ? "启用" : "禁用")}（{changed} 个 dll）。");
        return r;
    }

    // ---------------------------------------------------------------- 内部

    private static string ToRel(string gameRoot, string abs)
    {
        var rel = Path.GetRelativePath(gameRoot, abs);
        return rel.Replace('\\', '/');
    }

    private static void EnsureDir(string gameRoot, string relDir, List<string> created)
    {
        var parts = relDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = new System.Text.StringBuilder();

        foreach (var p in parts)
        {
            if (acc.Length > 0) acc.Append('/');
            acc.Append(p);

            var rel = acc.ToString();
            var abs = Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs))
            {
                Directory.CreateDirectory(abs);
                if (!created.Contains(rel, StringComparer.OrdinalIgnoreCase)) created.Add(rel);
            }
        }
    }

    private static void CopyAtomic(string src, string dst)
    {
        var tmp = dst + ".bd2mm.tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }
}
