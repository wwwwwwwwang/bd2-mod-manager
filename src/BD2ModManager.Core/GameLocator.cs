using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BD2ModManager.Core;

/// <summary>
/// 定位 BD2 PC 客户端。真实布局（实测）：
///   E:\Neowiz\Browndust2\Browndust2_10000001\BrownDust II.exe
///   E:\Neowiz\Browndust2\Browndust2_10000001\BrownDust II_Data\
/// 注意版本号子目录（Browndust2_10000001）会随更新变化，不能硬编码。
/// </summary>
public sealed class GameLocator
{
    public const string PrimaryExe = "BrownDust II.exe";
    public static readonly string[] ExeCandidates = { "BrownDust II.exe", "BrownDust2.exe" };

    private const int MaxDepth = 4;

    /// <summary>已知的发行商根目录。</summary>
    private static IEnumerable<string> KnownRoots()
    {
        yield return @"C:\ProgramData\Neowiz";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neowiz");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neowiz");

        if (OperatingSystem.IsWindows())
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { drives = Array.Empty<DriveInfo>(); }

            foreach (var d in drives)
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) continue;
                yield return Path.Combine(d.RootDirectory.FullName, "Neowiz");
            }
        }
    }

    public List<GameInstall> Find(IEnumerable<string>? extraRoots = null)
    {
        var results = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        roots.AddRange(KnownRoots());
        if (OperatingSystem.IsWindows()) roots.AddRange(RegistryRoots());
        if (extraRoots != null)
        {
            foreach (var r in extraRoots)
                if (!string.IsNullOrWhiteSpace(r)) roots.Add(r);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var g in ScanForGame(root, MaxDepth))
                results[g.Root] = g;
        }

        return results.Values
            .OrderByDescending(g => Directory.GetLastWriteTimeUtc(g.Root))
            .ToList();
    }

    /// <summary>从注册表 Uninstall 项取安装位置。</summary>
    public static IEnumerable<string> RegistryRoots()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        return RegistryRootsWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> RegistryRootsWindows()
    {
        var list = new List<string>();
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var view in views)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);

                var dirs = LookupIn(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
                    .Concat(LookupIn(hkcu, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));

                foreach (var d in dirs) list.Add(d);
            }
            catch { }
        }

        return list;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> LookupIn(RegistryKey baseKey, string sub)
    {
        var list = new List<string>();
        try
        {
            using var uninstall = baseKey.OpenSubKey(sub);
            if (uninstall == null) return list;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(name);
                if (k == null) continue;

                var display = k.GetValue("DisplayName") as string ?? "";
                if (display.IndexOf("browndust", StringComparison.OrdinalIgnoreCase) < 0 &&
                    display.IndexOf("brown dust", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var loc = k.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(loc)) list.Add(loc.TrimEnd('\\', '/'));

                // 有的安装包没有 InstallLocation，只能靠 DisplayIcon 的目录
                if (string.IsNullOrWhiteSpace(loc))
                {
                    var icon = k.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        var dir = Path.GetDirectoryName(icon.Trim('"').Split(',')[0]);
                        if (!string.IsNullOrWhiteSpace(dir)) list.Add(dir);
                    }
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>在 root 下递归寻找同时含 exe 与 *_Data 的目录。</summary>
    public static IEnumerable<GameInstall> ScanForGame(string root, int maxDepth)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            GameInstall? hit = null;
            try { hit = TryBuild(dir, "scan"); } catch { }
            if (hit != null) { yield return hit; continue; }

            if (depth >= maxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                // 跳过明显无关的大目录
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push((s, depth + 1));
            }
        }
    }

    /// <summary>把任意目录校验成一个游戏安装；不满足返回 null。</summary>
    public static GameInstall? TryBuild(string dir, string source)
    {
        string? exe = null;
        foreach (var cand in ExeCandidates)
        {
            var p = Path.Combine(dir, cand);
            if (File.Exists(p)) { exe = p; break; }
        }
        if (exe == null) return null;

        var stem = Path.GetFileNameWithoutExtension(exe);
        var data = Path.Combine(dir, stem + "_Data");
        if (!Directory.Exists(data)) return null;

        return new GameInstall(
            Path.GetFullPath(dir),
            Path.GetFullPath(exe),
            Path.GetFullPath(data),
            ReadUnityVersion(data),
            source);
    }

    private static readonly Regex UnityRe = new(@"20\d{2}\.\d+\.\d+[a-z]\d+", RegexOptions.Compiled);

    public static string? ReadUnityVersion(string dataDir)
    {
        var f = Path.Combine(dataDir, "globalgamemanagers");
        if (!File.Exists(f)) return null;

        try
        {
            // 版本串在文件前部，只读 4KB 足够
            var buf = new byte[4096];
            using var fs = File.OpenRead(f);
            int n = fs.Read(buf, 0, buf.Length);

            var sb = new System.Text.StringBuilder(n);
            for (int i = 0; i < n; i++)
                sb.Append(buf[i] >= 32 && buf[i] < 127 ? (char)buf[i] : '.');

            var m = UnityRe.Match(sb.ToString());
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }
}
