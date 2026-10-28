using System.Diagnostics;
using System.Security.Cryptography;

namespace BD2ModManager.Core;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string Sha256Bytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

public static class ProcessGuard
{
    /// <summary>游戏主进程名（不带 .exe）。</summary>
    public static readonly string[] GameProcessNames = { "BrownDust II", "BrownDust2", "Browndust2" };

    public static bool IsGameRunning()
    {
        foreach (var n in GameProcessNames)
        {
            try
            {
                var ps = Process.GetProcessesByName(n);
                if (ps.Length > 0)
                {
                    foreach (var p in ps) p.Dispose();
                    return true;
                }
            }
            catch
            {
                // 权限或平台问题，忽略
            }
        }
        return false;
    }

    public static List<string> RunningGameProcesses()
    {
        var found = new List<string>();
        foreach (var n in GameProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(n))
                {
                    found.Add($"{p.ProcessName} (PID {p.Id})");
                    p.Dispose();
                }
            }
            catch { }
        }
        return found;
    }
}
