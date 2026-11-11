using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BD2ModManager.Core;

/// <summary>诊断包导出选项。</summary>
public sealed class DiagnosticOptions
{
    /// <summary>输出目录；留空时放桌面（没有桌面则放状态目录下 diagnostics）。</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>单个配置文件最多打包多少字节，避免超大 cfg 撑爆诊断包。</summary>
    public int MaxConfigBytes { get; set; } = 512 * 1024;

    /// <summary>单个日志只读取末尾多少字节。</summary>
    public int MaxLogTailBytes { get; set; } = 256 * 1024;

    /// <summary>日志末尾最多保留多少行。</summary>
    public int MaxLogLines { get; set; } = 200;

    /// <summary>计算插件哈希的总预算；超出后只记录大小，不读文件。</summary>
    public int MaxPluginHashBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>诊断包导出结果。</summary>
public sealed class DiagnosticResult
{
    public bool Success { get; init; }
    public string? ZipPath { get; init; }
    public long ZipSize { get; init; }
    public List<string> Notes { get; } = new();
    public List<string> Errors { get; } = new();

    public static DiagnosticResult Fail(params string[] errors)
    {
        var r = new DiagnosticResult();
        r.Errors.AddRange(errors);
        return r;
    }
}

/// <summary>
/// 一键诊断包：把版本、环境、loader 状态、插件清单、配置和日志末尾打成 zip。
/// 只读取，不修改游戏目录；不打包插件 DLL、游戏文件、存档或任何网络内容。
/// 用户名与用户目录会替换成占位符，方便直接发到公开 issue。
/// </summary>
public static class Diagnostics
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record Check(string Level, string Text);

    private sealed record TextFile(string Name, string Rel, long Size, string Text, bool HasError = false);

    private sealed record PluginRow(string Name, bool Enabled, long Size, string? Sha256, string Path);

    public static DiagnosticResult Export(GameInstall? game, StateStore store, DiagnosticOptions? options = null)
    {
        options ??= new DiagnosticOptions();
        var notes = new List<string>();

        string? temp = null;
        try
        {
            var outDir = ResolveOutputDirectory(options.OutputDirectory, store);
            Directory.CreateDirectory(outDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var final = Path.Combine(outDir, $"BD2ModManager_diag_{stamp}.zip");
            var n = 2;
            while (File.Exists(final))
                final = Path.Combine(outDir, $"BD2ModManager_diag_{stamp}_{n++}.zip");

            temp = Path.Combine(outDir, $".bd2mm-diag-{Guid.NewGuid():n}.tmp");

            LoaderStatus? status = null;
            if (game != null)
            {
                try { status = LoaderInspector.Inspect(game, store); }
                catch (Exception ex) { notes.Add($"读取 loader 状态失败：{ex.Message}"); }
            }

            var components = new List<ComponentInfo>();
            if (game != null)
            {
                try { components = new ComponentManager(store).List(game); }
                catch (Exception ex) { notes.Add($"读取组件列表失败：{ex.Message}"); }
            }

            var configs = game == null ? new List<TextFile>() : ReadConfigs(game, options, notes);
            var logs = game == null ? new List<TextFile>() : ReadLogs(game, options, notes);
            var plugins = status?.Plugins ?? new List<PluginEntry>();
            var pluginRows = BuildPluginRows(game, plugins, options);

            var checks = BuildChecks(game, status, logs);
            var generatedAt = DateTimeOffset.Now;

            var report = BuildReport(game, status, pluginRows, components, configs, logs, checks,
                store, generatedAt, outDir);
            report = Redact(report, store);

            var summary = BuildSummary(game, status, pluginRows, components, configs, logs, checks,
                store, generatedAt);

            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "report.txt", report);

                var summaryJson = RedactJson(JsonSerializer.Serialize(summary, JsonOpts), store);
                WriteText(zip, "summary.json", summaryJson);

                foreach (var c in configs)
                    WriteText(zip, "configs/" + SafeEntryName(c.Rel), Redact(c.Text, store));
                foreach (var l in logs)
                    WriteText(zip, "logs/" + SafeEntryName(l.Name), Redact(l.Text, store));
            }

            File.Move(temp, final, overwrite: false);
            temp = null;

            var size = new FileInfo(final).Length;
            var r = new DiagnosticResult { Success = true, ZipPath = final, ZipSize = size };
            r.Notes.AddRange(notes);
            r.Notes.Add($"已导出 {plugins.Count} 个插件、{components.Count} 个组件、{configs.Count} 份配置、{logs.Count} 份日志。");
            return r;
        }
        catch (Exception ex)
        {
            return DiagnosticResult.Fail($"生成诊断包失败：{ex.Message}");
        }
        finally
        {
            if (temp != null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    // ------------------------------------------------------------ 采集

    private static string ResolveOutputDirectory(string? requested, StateStore store)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return Path.GetFullPath(requested);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            return Path.Combine(desktop, "BD2ModManager诊断");

        return Path.Combine(store.BaseDir, "diagnostics");
    }

    private static List<TextFile> ReadConfigs(GameInstall game, DiagnosticOptions options, List<string> notes)
    {
        var list = new List<TextFile>();

        var bepConfig = Path.Combine(game.Root, "BepInEx", "config");
        if (Directory.Exists(bepConfig))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(bepConfig, "*.cfg", SearchOption.TopDirectoryOnly)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = Path.Combine("BepInEx", "config", Path.GetFileName(f));
                    list.Add(ReadTextFile(f, rel, options.MaxConfigBytes, notes));
                }
            }
            catch (Exception ex) { notes.Add($"读取 BepInEx/config 失败：{ex.Message}"); }
        }

        var doorstop = Path.Combine(game.Root, "doorstop_config.ini");
        if (File.Exists(doorstop))
            list.Add(ReadTextFile(doorstop, "doorstop_config.ini", options.MaxConfigBytes, notes));

        return list;
    }

    private static TextFile ReadTextFile(string path, string rel, int maxBytes, List<string> notes)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }

        if (maxBytes > 0 && size > maxBytes)
        {
            notes.Add($"{rel} 超过 {maxBytes / 1024} KB，未打包内容。");
            return new TextFile(Path.GetFileName(path), rel, size, "", false);
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new TextFile(Path.GetFileName(path), rel, size, sr.ReadToEnd(), false);
        }
        catch (Exception ex)
        {
            notes.Add($"{rel} 读取失败：{ex.Message}");
            return new TextFile(Path.GetFileName(path), rel, size, "", false);
        }
    }

    private static List<TextFile> ReadLogs(GameInstall game, DiagnosticOptions options, List<string> notes)
    {
        var result = new List<TextFile>();
        var bep = Path.Combine(game.Root, "BepInEx");
        if (!Directory.Exists(bep)) return result;

        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(bep, "LogOutput.log*", SearchOption.TopDirectoryOnly));
            var probe = Path.Combine(bep, "SkinUnlock.probe.log");
            if (File.Exists(probe)) candidates.Add(probe);
        }
        catch (Exception ex)
        {
            notes.Add($"枚举日志失败：{ex.Message}");
            return result;
        }

        foreach (var path in candidates
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            var (tail, hasError) = ReadTail(path, options.MaxLogTailBytes, options.MaxLogLines, notes);
            result.Add(new TextFile(Path.GetFileName(path), Path.GetFileName(path), size, tail, hasError));
        }

        return result;
    }

    private static (string Tail, bool HasError) ReadTail(string path, int maxBytes, int maxLines, List<string> notes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            var want = (int)Math.Clamp(len, 0, Math.Clamp(maxBytes, 4 * 1024, 4 * 1024 * 1024));
            if (want <= 0) return ("", false);

            fs.Seek(-want, SeekOrigin.End);
            var buf = new byte[want];
            var read = fs.Read(buf, 0, want);
            var text = Encoding.UTF8.GetString(buf, 0, read);

            if (want < len)
            {
                var nl = text.IndexOf('\n');
                if (nl >= 0) text = text[(nl + 1)..];
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var limit = Math.Clamp(maxLines, 20, 2000);
            var tailLines = lines.Length > limit ? lines[^limit..] : lines;
            var tail = string.Join(Environment.NewLine, tailLines).TrimEnd();

            var hasError = tailLines.Any(l =>
                l.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("失败", StringComparison.Ordinal));
            return (tail, hasError);
        }
        catch (Exception ex)
        {
            notes.Add($"{Path.GetFileName(path)} 读取失败：{ex.Message}");
            return ("", false);
        }
    }

    private static List<PluginRow> BuildPluginRows(GameInstall? game, List<PluginEntry> plugins, DiagnosticOptions options)
    {
        var rows = new List<PluginRow>();
        long budget = Math.Max(0, options.MaxPluginHashBytes);

        foreach (var p in plugins)
        {
            long size = p.Size;
            try { size = new FileInfo(p.DllPath).Length; } catch { }

            string? hash = null;
            if (size > 0 && size <= budget)
            {
                try { hash = Hashing.Sha256File(p.DllPath); budget -= size; }
                catch { }
            }

            var rel = RelativeTo(game, p.DllPath);
            rows.Add(new PluginRow(p.Name, p.Enabled, size, hash, rel));
        }

        return rows;
    }

    private static string RelativeTo(GameInstall? game, string path)
    {
        if (game == null) return Path.GetFileName(path);
        try
        {
            var rel = Path.GetRelativePath(game.Root, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(path) : rel.Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static List<Check> BuildChecks(GameInstall? game, LoaderStatus? status, List<TextFile> logs)
    {
        var checks = new List<Check>();

        if (game == null)
        {
            checks.Add(new Check("WARN", "未找到游戏安装：游戏、loader 与插件检查已跳过。"));
            return checks;
        }

        if (status == null)
        {
            checks.Add(new Check("WARN", "无法读取 loader 状态。"));
        }
        else if (status.CorePresent)
        {
            checks.Add(new Check("OK", $"loader 完整：BepInEx {status.BepInExVersion ?? "未知"}，Doorstop {status.DoorstopVersion ?? "未知"}。"));
        }
        else if (status.IncompleteLoader)
        {
            checks.Add(new Check("WARN", "loader 不完整：winhttp.dll / doorstop_config.ini / BepInEx/core/BepInEx.dll 至少缺一项。"));
        }
        else if (status.ResidueOnly)
        {
            checks.Add(new Check("WARN", "loader 只剩缓存与日志残留，可用「清理残留」清除。"));
        }
        else
        {
            checks.Add(new Check("INFO", "未安装 loader（原生纯净状态）。"));
        }

        if (status != null && status.Present)
        {
            checks.Add(status.ManagedByUs
                ? new Check("INFO", $"loader 由本工具安装，包版本 {status.DetectedPackageVersion ?? "未知"}。")
                : new Check("INFO", "loader 不是本工具安装的，工具不会覆盖或删除它。"));
        }

        if (status != null)
        {
            var enabledUnlock = status.Plugins
                .Where(p => p.Enabled && IsUnlockName(p.Name))
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (enabledUnlock.Count > 1)
                checks.Add(new Check("WARN", "同时启用了多个解锁类插件，可能互相冲突：" + string.Join("、", enabledUnlock) + "。"));

            var dupes = status.Plugins
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (dupes.Count > 0)
                checks.Add(new Check("WARN", "plugins 下存在同名插件多份：" + string.Join("、", dupes) + "。"));

            if (status.Warnings.Count > 0)
                checks.Add(new Check("WARN", "loader 检查警告：" + string.Join("；", status.Warnings)));
        }

        var badLogs = logs.Where(l => l.HasError).Select(l => l.Name).ToList();
        if (badLogs.Count > 0)
            checks.Add(new Check("WARN", "日志里出现异常/错误关键字：" + string.Join("、", badLogs) + "（仅检查末尾摘录）。"));
        else if (logs.Count > 0)
            checks.Add(new Check("OK", "日志末尾未命中异常/错误关键字。"));

        if (status?.CorePresent == true)
        {
            var cfg = Path.Combine(game.Root, "BepInEx", "config", "BepInEx.cfg");
            if (!File.Exists(cfg))
                checks.Add(new Check("INFO", "BepInEx.cfg 尚未生成：第一次启动游戏后会自动创建。"));
        }

        return checks;
    }

    private static bool IsUnlockName(string name) =>
        name.Contains("unlock", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Covenant", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ 输出

    private static string BuildReport(
        GameInstall? game,
        LoaderStatus? status,
        List<PluginRow> pluginRows,
        List<ComponentInfo> components,
        List<TextFile> configs,
        List<TextFile> logs,
        List<Check> checks,
        StateStore store,
        DateTimeOffset generatedAt,
        string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== BD2ModManager 诊断报告 ===");
        sb.AppendLine($"生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"客户端版本：{ClientVersion()}");
        sb.AppendLine($"运行环境：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"操作系统：{RuntimeInformation.OSDescription}");
        sb.AppendLine($"系统架构：{RuntimeInformation.OSArchitecture} / 进程 {RuntimeInformation.ProcessArchitecture} / 64 位进程：{(Environment.Is64BitProcess ? "是" : "否")}");
        sb.AppendLine($"状态目录：{store.BaseDir}");
        sb.AppendLine($"输出目录：{outDir}");
        sb.AppendLine();
        sb.AppendLine("隐私说明：本报告不含游戏存档、账号、卡密或网络请求内容；");
        sb.AppendLine("          用户名与用户目录已替换为占位符，可直接附在公开 issue 中。");
        sb.AppendLine();

        sb.AppendLine("=== 游戏 ===");
        if (game == null)
        {
            sb.AppendLine("未找到游戏安装。可手动指定游戏目录后重新导出。");
        }
        else
        {
            sb.AppendLine($"路径：{game.Root}");
            sb.AppendLine($"可执行文件：{game.ExeName}");
            sb.AppendLine($"Unity：{game.UnityVersion ?? "未识别"}");
            sb.AppendLine($"识别来源：{game.Source}");
            sb.AppendLine($"运行中：{(ProcessGuard.IsGameRunning() ? "是" : "否")}");
        }
        sb.AppendLine();

        sb.AppendLine("=== Loader ===");
        if (status == null)
        {
            sb.AppendLine("状态：未知");
        }
        else
        {
            var state = status.CorePresent ? "完整"
                : status.IncompleteLoader ? "不完整"
                : status.ResidueOnly ? "仅残留"
                : "未安装";
            sb.AppendLine($"状态：{state}");
            sb.AppendLine($"BepInEx：{status.BepInExVersion ?? "未知"}");
            sb.AppendLine($"Doorstop：{status.DoorstopVersion ?? "未知"}");
            sb.AppendLine($"由本工具安装：{(status.ManagedByUs ? "是" : "否")}");
            sb.AppendLine($"包版本：{status.DetectedPackageVersion ?? "未知"}");
            if (status.Warnings.Count > 0)
                sb.AppendLine("警告：" + string.Join("；", status.Warnings));
        }
        sb.AppendLine();

        sb.AppendLine($"=== 插件（{pluginRows.Count}） ===");
        if (pluginRows.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var row in pluginRows)
            {
                var enabled = row.Enabled ? "启用" : "禁用";
                sb.AppendLine($"- [{enabled}] {row.Name}  {FormatSize(row.Size)}  sha256={row.Sha256 ?? "(未计算)"}  {row.Path}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"=== 组件（本工具管理，{components.Count}） ===");
        if (components.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var c in components)
            {
                sb.AppendLine($"- [{(c.Enabled ? "启用" : "禁用")}] {c.Name}  {c.FileCount} 个文件  {FormatSize(c.Size)}  {c.RelativeDir}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"=== 配置（已打包，{configs.Count}） ===");
        if (configs.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var c in configs)
                sb.AppendLine($"- {c.Rel}  {FormatSize(c.Size)}{(c.Text.Length == 0 ? "  (未读取内容)" : "")}");
        }
        sb.AppendLine();

        sb.AppendLine($"=== 日志（已打包末尾，{logs.Count}） ===");
        if (logs.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var l in logs)
                sb.AppendLine($"- {l.Name}  {FormatSize(l.Size)}");
        }
        sb.AppendLine();

        sb.AppendLine("=== 自动检查 ===");
        foreach (var c in checks)
            sb.AppendLine($"[{c.Level}] {c.Text}");
        sb.AppendLine();

        sb.AppendLine("=== 说明 ===");
        sb.AppendLine("诊断包只包含本报告、summary.json、配置文本与日志末尾；");
        sb.AppendLine("插件 DLL 与游戏文件不会被打包。");
        return sb.ToString();
    }

    private static Dictionary<string, object?> BuildSummary(
        GameInstall? game,
        LoaderStatus? status,
        List<PluginRow> pluginRows,
        List<ComponentInfo> components,
        List<TextFile> configs,
        List<TextFile> logs,
        List<Check> checks,
        StateStore store,
        DateTimeOffset generatedAt)
    {
        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["generatedAt"] = generatedAt.ToString("O"),
            ["clientVersion"] = ClientVersion(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["stateDir"] = store.BaseDir,
            ["game"] = game == null ? null : new
            {
                root = game.Root,
                exe = game.ExeName,
                unity = game.UnityVersion,
                source = game.Source,
                running = ProcessGuard.IsGameRunning()
            },
            ["loader"] = status == null ? null : new
            {
                present = status.Present,
                corePresent = status.CorePresent,
                incomplete = status.IncompleteLoader,
                residueOnly = status.ResidueOnly,
                managedByUs = status.ManagedByUs,
                bepInExVersion = status.BepInExVersion,
                doorstopVersion = status.DoorstopVersion,
                packageVersion = status.DetectedPackageVersion,
                warnings = status.Warnings
            },
            ["plugins"] = pluginRows,
            ["components"] = components.Select(c => new
            {
                name = c.Name,
                enabled = c.Enabled,
                files = c.FileCount,
                size = c.Size,
                relativeDir = c.RelativeDir,
                source = c.Source
            }).ToArray(),
            ["configs"] = configs.Select(c => new
            {
                name = c.Name,
                path = c.Rel,
                size = c.Size
            }).ToArray(),
            ["logs"] = logs.Select(l => new
            {
                name = l.Name,
                size = l.Size,
                hasError = l.HasError
            }).ToArray(),
            ["checks"] = checks.Select(c => new
            {
                level = c.Level,
                text = c.Text
            }).ToArray()
        };
    }

    private static string ClientVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(Diagnostics).Assembly;
            var v = asm.GetName().Version;
            if (v != null && v.Major > 0) return v.ToString(3);
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static IEnumerable<(string Needle, string Replacement)> Redactions(StateStore store)
    {
        var baseDir = store.BaseDir;
        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return (baseDir, @"%APPDATA%\BD2ModManager");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            yield return (profile, "%USERPROFILE%");

        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
            yield return (user, "%USER%");
    }

    private static string Redact(string text, StateStore store)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var (needle, replacement) in Redactions(store))
            text = text.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);

        return text;
    }

    /// <summary>JSON 里的反斜杠会被转义成两个，必须按转义后的形式替换。</summary>
    private static string RedactJson(string json, StateStore store)
    {
        if (string.IsNullOrEmpty(json)) return json;

        foreach (var (needle, replacement) in Redactions(store))
        {
            var escapedNeedle = needle.Replace("\\", "\\\\");
            var escapedReplacement = replacement.Replace("\\", "\\\\");
            json = json.Replace(escapedNeedle, escapedReplacement, StringComparison.OrdinalIgnoreCase);
        }

        return json;
    }

    private static string SafeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch == '/' || ch == '\\' || invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";
}
