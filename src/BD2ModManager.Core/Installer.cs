using System.Text.Json;

namespace BD2ModManager.Core;

/// <summary>
/// 安装 / 卸载 loader。核心约定：
///  1. 安装前先校验包内每个文件的 sha256（供应链校验，包被换过就直接拒绝）。
///  2. 覆盖任何已存在文件前先备份；只清理由自己创建的文件。
///  3. 卸载只删除「清单里记录过、且哈希仍匹配」的文件；用户改过的一律保留并报告。
///  4. 状态文件放在游戏目录之外，BepInEx 目录被手工删掉也不影响卸载。
/// </summary>
public sealed class LoaderInstaller
{
    private readonly StateStore _store;

    private static readonly JsonSerializerOptions ManifestOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public LoaderInstaller(StateStore store) => _store = store;

    // ---------------------------------------------------------------- 包校验

    public static PackageManifest? LoadManifest(string packageDir)
    {
        var p = Path.Combine(packageDir, "manifest.json");
        if (!File.Exists(p)) return null;
        try
        {
            return JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(p), ManifestOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验包完整性。返回 null 表示通过，否则返回问题描述。</summary>
    public static string? VerifyPackage(string packageDir, PackageManifest m)
    {
        foreach (var f in m.Files)
        {
            var src = Path.Combine(packageDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) return $"包内缺少文件：{f.Path}";

            var actual = Hashing.Sha256File(src);
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"文件哈希不匹配（包可能被篡改）：{f.Path}";
        }
        return null;
    }

    // ---------------------------------------------------------------- 安装

    public OpResult Install(GameInstall game, string packageDir, bool force = false)
    {
        var r = new OpResult();

        var m = LoadManifest(packageDir);
        if (m == null) return OpResult.Fail($"包清单无效或缺失：{Path.Combine(packageDir, "manifest.json")}");

        var integrity = VerifyPackage(packageDir, m);
        if (integrity != null) return OpResult.Fail(integrity);

        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先完全退出游戏再安装。");

        var existing = _store.TryLoad(game.Root);
        var status = LoaderInspector.Inspect(game, _store);

        if (status.Present && existing == null && !force)
            return OpResult.Fail(
                "该游戏目录已存在 BepInEx，但不是本工具安装的。",
                "为避免破坏他人安装，安装已中止。",
                "如确认要覆盖，请使用强制模式，或先手工清理 winhttp.dll / doorstop_config.ini / BepInEx 目录。");

        var state = existing ?? new InstallState
        {
            GameRoot = game.Root,
            PackageName = m.Package,
            PackageVersion = m.Loader?.Version ?? "unknown",
            InstalledAt = DateTimeOffset.Now
        };

        var backupDir = _store.BackupDirFor(game.Root);
        var createdDirs = new List<string>();
        var addedPaths = new List<string>();
        var touched = new List<string>();

        try
        {
            // 目录（深到浅都建），逐级记录「本次新建」的目录
            foreach (var d in m.Dirs.OrderBy(x => x.Length))
                EnsureDir(game.Root, d, createdDirs);

            foreach (var f in m.Files)
            {
                var rel = f.Path;
                var src = Path.Combine(packageDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var dst = Path.Combine(game.Root, rel.Replace('/', Path.DirectorySeparatorChar));

                var relDir = Path.GetDirectoryName(rel.Replace('\\', '/'))?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(relDir))
                    EnsureDir(game.Root, relDir, createdDirs);

                if (File.Exists(dst))
                {
                    var cur = Hashing.Sha256File(dst);
                    if (string.Equals(cur, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        r.Info($"已是最新，跳过：{rel}");
                        continue;
                    }

                    // 备份后再覆盖
                    var bakRel = rel.Replace('/', Path.DirectorySeparatorChar);
                    var bakAbs = Path.Combine(backupDir, bakRel);
                    var bakParent = Path.GetDirectoryName(bakAbs);
                    if (!string.IsNullOrEmpty(bakParent)) Directory.CreateDirectory(bakParent);
                    File.Copy(dst, bakAbs, overwrite: true);

                    state.Replaced.RemoveAll(x => x.Path == rel);
                    state.Replaced.Add(new StateFile { Path = rel, Sha256 = cur, Backup = bakRel });
                    r.Info($"备份并覆盖：{rel}");
                }
                else
                {
                    state.Added.RemoveAll(x => x.Path == rel);
                    state.Added.Add(new StateFile { Path = rel, Sha256 = f.Sha256 });
                    addedPaths.Add(dst);
                }

                touched.Add(dst);
                CopyAtomic(src, dst);
            }

            // 合并目录记录
            foreach (var d in createdDirs)
                if (!state.Dirs.Contains(d)) state.Dirs.Add(d);

            state.InstalledAt = DateTimeOffset.Now;
            state.PackageName = m.Package;
            state.PackageVersion = m.Loader?.Version ?? state.PackageVersion;
            _store.Save(state);

            r.Success = true;
            r.Info($"安装完成：BepInEx {state.PackageVersion}（Doorstop {m.Loader?.Doorstop}）");
            r.Info($"新增 {state.Added.Count} 个文件，覆盖 {state.Replaced.Count} 个文件。");
            return r;
        }
        catch (Exception ex)
        {
            r.Error($"安装失败：{ex.Message}");
            TryRollback(game, state, addedPaths, backupDir, r);
            return r;
        }
    }

    // ---------------------------------------------------------------- 卸载

    public OpResult Uninstall(GameInstall game, bool force = false)
    {
        var r = new OpResult();

        var state = _store.TryLoad(game.Root);
        if (state == null)
        {
            var st = LoaderInspector.Inspect(game, _store);
            if (!st.Present) return OpResult.Fail("未检测到 BepInEx，无需卸载。");

            return force
                ? OpResult.Fail("强制卸载尚未实现：为避免误删他人文件，请手工处理。")
                : OpResult.Fail(
                    "该 BepInEx 不是本工具安装的（无安装记录）。",
                    "工具不会删除来源不明的文件。",
                    "如需彻底移除，请手工删除：winhttp.dll、doorstop_config.ini、.doorstop_version、BepInEx 目录。");
        }

        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先完全退出游戏再卸载。");

        var backupDir = _store.BackupDirFor(game.Root);
        var skipped = 0;

        // 0) 先清第三方组件：它们住在 BepInEx/plugins 下，不清掉目录永远清不干净
        var comp = new ComponentManager(_store).RemoveAll(game);
        foreach (var l in comp.Log) r.Info(l);
        foreach (var e in comp.Errors) r.Error(e);

        // 1) 删除自己新增的文件
        foreach (var f in state.Added)
        {
            var dst = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(dst)) continue;

            try
            {
                var cur = Hashing.Sha256File(dst);
                if (!string.Equals(cur, f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    r.Info($"保留（已被修改）：{f.Path}");
                    skipped++;
                    continue;
                }
                File.Delete(dst);
                r.Info($"删除：{f.Path}");
            }
            catch (Exception ex)
            {
                r.Error($"删除失败 {f.Path}：{ex.Message}");
                skipped++;
            }
        }

        // 2) 还原被覆盖的文件
        foreach (var f in state.Replaced)
        {
            var dst = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            var bak = Path.Combine(backupDir, (f.Backup ?? f.Path).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(bak)) { r.Info($"备份缺失，跳过还原：{f.Path}"); skipped++; continue; }

            try
            {
                var parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(bak, dst, overwrite: true);
                r.Info($"还原：{f.Path}");
            }
            catch (Exception ex)
            {
                r.Error($"还原失败 {f.Path}：{ex.Message}");
                skipped++;
            }
        }

        // 2.5) 清运行时产物：缓存、日志、自带配置都是 loader 第一次启动时自己生成的，
        //      不在这里删掉，BepInEx 目录就会留个空壳，下次检测还会被当成「他人安装」。
        //      卸载是彻底清场，连插件记下的皮肤选择一起删。
        CleanRuntimeTraces(game, r, includePluginData: true);

        // 3) 清理空目录（深到浅）
        foreach (var d in state.Dirs.OrderByDescending(x => x.Length))
        {
            var dir = Path.Combine(game.Root, d.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    r.Info($"移除空目录：{d}");
                }
            }
            catch { }
        }

        // 4) 清状态
        try
        {
            _store.Delete(game.Root);
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            PruneEmptyBaseDirs();
        }
        catch { }

        var remain = LoaderInspector.Inspect(game, _store);
        if (remain.Present)
        {
            r.Info("注意：BepInEx 目录里仍有非本工具写入的文件（多为手工放的插件），已原样保留。");
            skipped++;
        }

        r.Success = true;
        r.Info(skipped == 0 ? "卸载完成，已恢复到安装前状态。" : $"卸载完成，{skipped} 个文件被保留（详见上方日志）。");
        return r;
    }

    // ---------------------------------------------------------------- 残留清理

    /// <summary>loader 启动时自己生成的配置文件，名字固定，可以安全清理。</summary>
    private static readonly string[] RuntimeConfigFiles =
    {
        "BepInEx/config/BepInEx.cfg",
        "BepInEx/config/com.bepis.bepinex.configurationmanager.cfg",
        "BepInEx/config/com.suiren.bd2.skinunlock.cfg"
    };

    /// <summary>
    /// 内置插件运行时写下的文件。probe.log 是纯粹的诊断输出，删掉无副作用；
    /// selection.txt 记着玩家选过的皮肤，属于用户数据，只在「卸载 loader」时清。
    /// </summary>
    private const string ProbeLogRel = "BepInEx/SkinUnlock.probe.log";
    private const string SelectionRel = "BepInEx/config/com.suiren.bd2.skinunlock.selection.txt";

    /// <summary>
    /// 清理 loader 的运行时产物（缓存、日志、自带配置）。
    /// 只碰已知名字的文件：plugins 下的插件、config 下其他 cfg 一律不动。
    /// </summary>
    private int CleanRuntimeTraces(GameInstall game, OpResult r, bool includePluginData = false)
    {
        var bepDir = Path.Combine(game.Root, "BepInEx");
        if (!Directory.Exists(bepDir)) return 0;

        var removed = 0;

        // 缓存：纯派生数据，删了游戏下次自己重建
        var cache = Path.Combine(bepDir, "cache");
        if (Directory.Exists(cache))
        {
            try
            {
                Directory.Delete(cache, recursive: true);
                r.Info("清理缓存：BepInEx/cache");
                removed++;
            }
            catch (Exception ex) { r.Error($"清理缓存失败：{ex.Message}"); }
        }

        // 日志（含轮转出的旧副本）
        try
        {
            foreach (var f in Directory.EnumerateFiles(bepDir, "LogOutput.log*"))
            {
                try
                {
                    File.Delete(f);
                    r.Info($"清理日志：BepInEx/{Path.GetFileName(f)}");
                    removed++;
                }
                catch (Exception ex) { r.Error($"清理日志失败 {Path.GetFileName(f)}：{ex.Message}"); }
            }
        }
        catch { }

        // 内置插件的诊断日志：重启游戏会重新生成，删掉无副作用
        var probe = Path.Combine(game.Root, ProbeLogRel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(probe))
        {
            try
            {
                File.Delete(probe);
                r.Info($"清理日志：{ProbeLogRel}");
                removed++;
            }
            catch (Exception ex) { r.Error($"清理日志失败 {ProbeLogRel}：{ex.Message}"); }
        }

        foreach (var rel in RuntimeConfigFiles)
        {
            var p = Path.Combine(game.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) continue;
            try
            {
                File.Delete(p);
                r.Info($"清理配置：{rel}");
                removed++;
            }
            catch (Exception ex) { r.Error($"清理配置失败 {rel}：{ex.Message}"); }
        }

        // 玩家选过的皮肤：只在卸载 loader 时清，清理残留时留着，
        // 否则用户只是清一下缓存，选好的皮肤全丢。
        if (includePluginData)
        {
            var sel = Path.Combine(game.Root, SelectionRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(sel))
            {
                try
                {
                    File.Delete(sel);
                    r.Info($"清理插件数据：{SelectionRel}");
                    removed++;
                }
                catch (Exception ex) { r.Error($"清理插件数据失败 {SelectionRel}：{ex.Message}"); }
            }
        }

        RemoveEmptyTree(bepDir);
        return removed;
    }

    /// <summary>自内向外删空目录；BepInEx 空了连它一起删。</summary>
    private static void RemoveEmptyTree(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                                .OrderByDescending(x => x.Length);
            foreach (var d in dirs)
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch { }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch { }
    }

    /// <summary>
    /// 清理纯残留：loader 核心（winhttp.dll / BepInEx/core）已经不在，目录里只剩缓存与日志。
    /// 核心仍在时拒绝，那种情况必须走正常还原，免得把一份能用的 loader 拆坏。
    /// </summary>
    public OpResult CleanResidue(GameInstall game)
    {
        var r = new OpResult();
        var st = LoaderInspector.Inspect(game, _store);

        if (!st.Present)
            return OpResult.Fail("目录里没有 BepInEx 痕迹，无需清理。");

        if (st.CorePresent)
            return OpResult.Fail(
                "loader 核心仍在，不能用残留清理。",
                "请用「启用 Mod 支持」开关走正常还原流程。");

        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先完全退出游戏。");

        var n = CleanRuntimeTraces(game, r);

        // 剩下的插件不是本工具写的，只报告不动手
        foreach (var p in st.Plugins)
            r.Info($"保留（非本工具安装）：BepInEx/plugins/{p.Name}");

        r.Success = true;
        if (n > 0)
            r.Info($"残留清理完成，共处理 {n} 项。");
        else if (st.Plugins.Count > 0)
            r.Info($"loader 的缓存与日志早就没了，没什么要清理的；plugins 里的 {st.Plugins.Count} 个插件不是本工具装的，保留原样。");
        else
            r.Info("没有可清理的残留。");

        return r;
    }

    // ---------------------------------------------------------------- 插件开关

    public OpResult SetPluginEnabled(string gameRoot, string dllPath, bool enabled)
    {
        var r = new OpResult();
        if (ProcessGuard.IsGameRunning())
            return OpResult.Fail("游戏正在运行，请先退出再切换插件。");

        try
        {
            if (enabled)
            {
                if (!dllPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    return OpResult.Fail("该插件当前已是启用状态。");
                var target = dllPath[..^".disabled".Length];
                if (File.Exists(target)) return OpResult.Fail($"目标文件已存在：{target}");
                File.Move(dllPath, target);
                r.Info($"已启用：{Path.GetFileName(target)}");
            }
            else
            {
                if (dllPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    return OpResult.Fail("该插件当前已是禁用状态。");
                File.Move(dllPath, dllPath + ".disabled");
                r.Info($"已禁用：{Path.GetFileName(dllPath)}");
            }
            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"切换失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 逐级创建目录，并把「本次真正新建」的每一层都记录下来，
    /// 否则卸载时会漏掉中间层（CreateDirectory 会连带建父目录）。
    /// </summary>
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
                if (!created.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    created.Add(rel);
            }
        }
    }

    /// <summary>清掉自己建的空状态目录，避免留下空壳。</summary>
    private void PruneEmptyBaseDirs()
    {
        foreach (var d in new[] { _store.StateDir, _store.BackupRoot, _store.BaseDir })
        {
            try
            {
                if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
            }
            catch { }
        }
    }

    private static void CopyAtomic(string src, string dst)    {
        var tmp = dst + ".bd2mm.tmp";
        File.Copy(src, tmp, overwrite: true);
        if (File.Exists(dst))
        {
            // 保留目标属性（部分 loader 文件带只读标记时先清掉）
            try
            {
                var attrs = File.GetAttributes(dst);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
        File.Move(tmp, dst, overwrite: true);
    }

    private void TryRollback(GameInstall game, InstallState state, List<string> addedPaths,
        string backupDir, OpResult r)
    {
        r.Info("开始回滚…");
        foreach (var p in addedPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
        foreach (var f in state.Replaced)
        {
            try
            {
                var dst = Path.Combine(game.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
                var bak = Path.Combine(backupDir, (f.Backup ?? f.Path).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(bak)) File.Copy(bak, dst, overwrite: true);
            }
            catch { }
        }
        try { _store.Delete(game.Root); } catch { }
        r.Info("回滚结束。");
    }
}
