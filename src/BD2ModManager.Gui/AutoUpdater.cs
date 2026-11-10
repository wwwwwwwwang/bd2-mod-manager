using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace BD2ModManager.Gui;

/// <summary>
/// 下载 Release 里的单文件 exe，校验 SHA256，然后替换当前程序并重启。
/// 只适用于官方发布的 BD2ModManager.exe；开发态（dotnet run）会拒绝执行。
/// </summary>
public static class AutoUpdater
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient ChecksumHttp = CreateChecksumClient();

    public static async Task<string> DownloadAsync(
        UpdateCheckResult update,
        string targetDirectory,
        IProgress<double>? progress = null,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.CanAutoUpdate)
            throw new InvalidOperationException("这个 Release 没有提供可自动更新的 exe 或校验文件。");
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("请先选择更新包下载目录。");

        var dir = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(dir);

        var assetName = Path.GetFileName(update.AssetName!);
        if (!string.Equals(assetName, update.AssetName, StringComparison.Ordinal))
            throw new InvalidOperationException("更新包文件名不合法。");

        var target = Path.Combine(dir, assetName);
        if (File.Exists(target)) File.Delete(target);

        using var response = await Http.GetAsync(
            update.DownloadUrl!, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            var lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                if (total is > 0)
                {
                    var percent = (int)(received * 100 / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent / 100.0);
                    }
                }
            }

            if (total is > 0 && received != total.Value)
                throw new IOException($"更新包下载不完整：{received}/{total}");
        }

        stage?.Report("正在校验更新包…");
        await VerifyChecksumAsync(target, update, cancellationToken);
        stage?.Report("校验通过，准备安装…");
        progress?.Report(1.0);
        return target;
    }

    /// <summary>
    /// Windows 允许重命名正在运行的 exe，但不能直接覆盖。先把当前 exe 改成 .old，
    /// 再把新 exe 放到原路径并启动；失败时尽量回滚，避免用户只剩一个坏文件。
    /// </summary>
    public static void ApplyAndRestart(string downloadedExe)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current))
            throw new InvalidOperationException("无法定位当前程序路径。");

        var fileName = Path.GetFileName(current);
        if (!string.Equals(fileName, "BD2ModManager.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前不是官方发布的 BD2ModManager.exe，无法自动替换。");

        var dir = Path.GetDirectoryName(current)!;
        var backup = current + $".old-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        File.Move(current, backup);

        var copied = false;
        try
        {
            File.Copy(downloadedExe, current, overwrite: true);
            copied = true;
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = current,
                WorkingDirectory = dir,
                UseShellExecute = false
            });
            if (process == null)
                throw new InvalidOperationException("新版本启动失败。");
        }
        catch
        {
            if (copied)
            {
                try { File.Delete(current); } catch { }
            }
            try
            {
                if (File.Exists(backup)) File.Move(backup, current);
            }
            catch { }
            throw;
        }
    }

    /// <summary>新版本启动后清理上一次更新留下的 .old 文件。</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(current)) return;
            if (current.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return;

            var dir = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var fileName = Path.GetFileName(current);

            foreach (var path in Directory.EnumerateFiles(dir, fileName + ".old*"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }

    private static async Task VerifyChecksumAsync(
        string file,
        UpdateCheckResult update,
        CancellationToken cancellationToken)
    {
        // GitHub Release API 的 asset digest 是首选：不依赖单独下载 SHA256SUMS.txt。
        var expected = update.ExpectedSha256?.Trim();

        if (string.IsNullOrWhiteSpace(expected))
        {
            if (string.IsNullOrWhiteSpace(update.ChecksumUrl))
                throw new InvalidOperationException("这个 Release 没有提供 SHA256 校验信息。");

            var text = await DownloadChecksumTextAsync(update.ChecksumUrl, cancellationToken);

            foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                string.Equals(parts[1].TrimStart('*'), update.AssetName, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0];
                break;
            }
            }
        }

        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("SHA256SUMS.txt 中没有找到更新包。");

        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidOperationException("更新包 SHA256 格式不合法。");

        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新包 SHA256 校验失败，已取消更新。");
    }

    private static async Task<string> DownloadChecksumTextAsync(string url, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await ChecksumHttp.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "下载 SHA256SUMS.txt 失败，请检查网络后重试，或使用“手动下载”。", last);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BD2ModManager/auto-update");
        return client;
    }

    private static HttpClient CreateChecksumClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BD2ModManager/auto-update");
        return client;
    }
}
