using System.IO.Compression;
using System.Reflection;

namespace BD2ModManager.Core;

/// <summary>
/// 一个已解开的 loader 包。目录、zip、程序内嵌资源三种来源统一成「目录」，
/// 交给 <see cref="LoaderInstaller"/> 时只需要目录。
/// zip / 内嵌资源会解到临时目录，<see cref="Dispose"/> 时清理。
/// </summary>
public sealed class PackageHandle : IDisposable
{
    private readonly string? _tempDir;

    internal PackageHandle(string dir, string? tempDir, string? problem)
    {
        Directory = dir;
        _tempDir = tempDir;
        Problem = problem;
        Manifest = problem == null ? LoaderInstaller.LoadManifest(dir) : null;
        if (Manifest == null && problem == null)
            Problem = $"包清单无效或缺失：{Path.Combine(dir, "manifest.json")}";
    }

    /// <summary>包目录（已解开）。</summary>
    public string Directory { get; }

    public PackageManifest? Manifest { get; }

    /// <summary>不可用的原因；为 null 表示可用。</summary>
    public string? Problem { get; }

    public bool IsValid => Manifest != null && Problem == null;

    public string? Version => Manifest?.Loader?.Version;

    public void Dispose()
    {
        if (_tempDir == null) return;
        try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

/// <summary>
/// loader 包来源解析。查找顺序（GUI 用）：程序目录旁的 zip → 包目录 → 程序内嵌资源，
/// 都找不到才让用户手动指定。
/// </summary>
public static class LoaderPackage
{
    /// <summary>内嵌资源名（csproj 里 LogicalName 固定成这个）。</summary>
    public const string ResourceName = "bd2-loader.zip";

    /// <summary>打开目录或 zip。</summary>
    public static PackageHandle OpenPath(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) return new PackageHandle(path, null, null);
            if (File.Exists(path)) return OpenZip(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            return new PackageHandle(path, null, $"读取包失败：{ex.Message}");
        }
        return new PackageHandle(path, null, $"包不存在：{path}");
    }

    /// <summary>从程序所在目录找：bd2-loader.zip、loader\manifest.json、当前目录。</summary>
    public static PackageHandle? FindBeside(string baseDir)
    {
        var candidates = new List<string>();

        try
        {
            foreach (var zip in System.IO.Directory.EnumerateFiles(baseDir, "*.zip"))
            {
                if (Path.GetFileName(zip).StartsWith("bd2-loader", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(zip);
            }
        }
        catch { }

        candidates.Add(Path.Combine(baseDir, "loader"));
        candidates.Add(Path.Combine(baseDir, "bd2-loader"));

        foreach (var c in candidates)
        {
            if (!File.Exists(c) && !System.IO.Directory.Exists(c)) continue;
            var h = OpenPath(c);
            if (h.IsValid) return h;
            h.Dispose();
        }
        return null;
    }

    /// <summary>从程序集内嵌资源找。发布版把 loader 包嵌进 exe，做到单文件可用。</summary>
    public static PackageHandle? FindEmbedded(Assembly? asm = null)
    {
        asm ??= Assembly.GetEntryAssembly();
        if (asm == null) return null;

        try
        {
            using var s = asm.GetManifestResourceStream(ResourceName);
            if (s == null) return null;

            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return OpenZip(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按「旁边 → 内嵌」顺序自动找包。</summary>
    public static PackageHandle? AutoLocate(string baseDir, Assembly? asm = null) =>
        FindBeside(baseDir) ?? FindEmbedded(asm);

    private static PackageHandle OpenZip(byte[] bytes)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bd2mm-pkg-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            System.IO.Directory.CreateDirectory(tmp);

            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            ExtractSafe(zip, tmp);

            // 有的包把内容套了一层根目录
            var inner = Path.Combine(tmp, "bd2-loader");
            var root = File.Exists(Path.Combine(tmp, "manifest.json")) ? tmp
                : File.Exists(Path.Combine(inner, "manifest.json")) ? inner
                : tmp;

            var h = new PackageHandle(root, tmp, null);
            if (h.IsValid) return h;

            // 不可用就别留着临时目录
            var problem = h.Problem;
            h.Dispose();
            return new PackageHandle(tmp, null, problem);
        }
        catch (Exception ex)
        {
            try { System.IO.Directory.Delete(tmp, recursive: true); } catch { }
            return new PackageHandle(tmp, null, $"解包失败：{ex.Message}");
        }
    }

    /// <summary>防目录穿越：条目路径必须落在目标目录内。</summary>
    private static void ExtractSafe(ZipArchive zip, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith('/')) continue;

            var dest = Path.GetFullPath(Path.Combine(target, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包内路径非法：{e.FullName}");

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            e.ExtractToFile(dest, overwrite: true);
        }
    }
}
