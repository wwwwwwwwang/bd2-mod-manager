namespace BD2ModManager.Core;

/// <summary>一个被识别出来的游戏安装。</summary>
public sealed record GameInstall(
    string Root,
    string ExePath,
    string DataDir,
    string? UnityVersion,
    string Source)
{
    public string ExeName => Path.GetFileName(ExePath);

    public override string ToString() => $"{Root}  [{Source}]";
}

/// <summary>已安装的 BepInEx 状态。</summary>
public sealed class LoaderStatus
{
    public required bool Present { get; init; }

    /// <summary>
    /// loader 核心是否还在（winhttp.dll 或 BepInEx/core/BepInEx.dll）。
    /// false 表示目录里只剩缓存、日志之类的运行时痕迹，游戏根本不会加载任何插件。
    /// </summary>
    public bool CorePresent { get; init; }

    /// <summary>有痕迹但核心已不在：属于本工具卸载后的残留，不该算「他人安装」。</summary>
    public bool ResidueOnly => Present && !CorePresent;

    public string? DoorstopVersion { get; init; }
    public string? BepInExVersion { get; init; }
    /// <summary>是否由本工具安装（存在状态文件）。false 表示他人/手工安装。</summary>
    public bool ManagedByUs { get; init; }
    public string? DetectedPackageVersion { get; init; }
    public List<PluginEntry> Plugins { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public static LoaderStatus NotFound() => new() { Present = false };
}

public sealed record PluginEntry(string DllPath, string Name, bool Enabled, long Size);

/// <summary>安装状态文件，卸载的唯一依据。</summary>
public sealed class InstallState
{
    public int Schema { get; set; } = 1;
    public string GameRoot { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    public List<StateFile> Added { get; set; } = new();
    public List<StateFile> Replaced { get; set; } = new();
    public List<string> Dirs { get; set; } = new();
}

public sealed class StateFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>被覆盖文件的备份相对路径（相对备份根）。仅 Replaced 使用。</summary>
    public string? Backup { get; set; }
}

/// <summary>包清单 manifest.json。</summary>
public sealed class PackageManifest
{
    public int Schema { get; set; }
    public string Package { get; set; } = "";
    public LoaderInfo? Loader { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
    public List<string> Dirs { get; set; } = new();
    public Dictionary<string, string> Entrypoints { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("target_game")]
    public TargetGame? TargetGame { get; set; }
}

public sealed class LoaderInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Doorstop { get; set; }
    public string Arch { get; set; } = "x64";
}

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class TargetGame
{
    public string Exe { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string? Unity { get; set; }
}

/// <summary>操作结果。</summary>
public sealed class OpResult
{
    public bool Success { get; set; }
    public List<string> Log { get; } = new();
    public List<string> Errors { get; } = new();

    public void Info(string m) => Log.Add(m);
    public void Error(string m) { Errors.Add(m); }

    public static OpResult Ok() => new() { Success = true };
    public static OpResult Fail(params string[] errs)
    {
        var r = new OpResult();
        r.Errors.AddRange(errs);
        return r;
    }
}
