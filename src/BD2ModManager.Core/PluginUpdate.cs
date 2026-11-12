using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BD2ModManager.Core;

/// <summary>
/// 解锁插件的独立更新清单。插件跟主程序分开发布，清单只描述一个 DLL，
/// 避免每次插件小改都重新下载几十 MB 的 GUI。
/// </summary>
public sealed class PluginManifest
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "BD2SkinUnlock";
    public string Version { get; set; } = "";
    public string MinAppVersion { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? Notes { get; set; }
}

public sealed class PluginUpdateCheck
{
    public bool Success { get; init; }
    public bool Available { get; init; }
    public string CurrentVersion { get; init; } = "";
    public PluginManifest? Latest { get; init; }
    public string? Error { get; init; }

    public static PluginUpdateCheck Failed(string error) => new() { Success = false, Error = error };
}

public sealed record PluginApplyOutcome(bool Success, string Message, bool Pending);

/// <summary>
/// 插件更新：检查清单 → 下载到本地 pending → 退出游戏后原子替换组件文件。
/// 只接受清单里带 SHA256 的 DLL；不执行下载内容，也不做进程注入。
/// </summary>
public static class PluginUpdater
{
    public const string CurrentPluginVersion = "1.3.0";

    public const string ManifestUrl =
        "https://raw.githubusercontent.com/wwwwwwwwang/bd2-mod-manager/main/plugin/plugin.json";

    private const string PluginFileName = "BD2SkinUnlock.dll";
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    public static async Task<PluginUpdateCheck> CheckAsync(
        string currentAppVersion,
        string currentPluginVersion = CurrentPluginVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.ParseAdd("BD2ModManager/plugin-update");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
            };

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return PluginUpdateCheck.Failed($"插件清单返回 HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var manifest = ParseManifest(doc.RootElement);
            if (manifest == null)
                return PluginUpdateCheck.Failed("插件清单格式不正确");

            if (!string.IsNullOrWhiteSpace(manifest.MinAppVersion) &&
                IsNewer(manifest.MinAppVersion, currentAppVersion))
            {
                return PluginUpdateCheck.Failed(
                    $"插件 {manifest.Version} 需要主程序 {manifest.MinAppVersion}，当前为 {currentAppVersion}");
            }

            return new PluginUpdateCheck
            {
                Success = true,
                Available = IsNewer(manifest.Version, currentPluginVersion),
                CurrentVersion = currentPluginVersion,
                Latest = manifest,
            };
        }
        catch (OperationCanceledException)
        {
            return PluginUpdateCheck.Failed("插件更新检查超时");
        }
        catch (Exception ex)
        {
            return PluginUpdateCheck.Failed(ex.Message);
        }
    }

    /// <summary>解析清单；URL 必须是 HTTPS，SHA256 必须是 64 位十六进制。</summary>
    public static PluginManifest? ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        var version = Str(root, "version")?.Trim();
        var url = Str(root, "url")?.Trim();
        var sha = Str(root, "sha256")?.Trim();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(sha))
            return null;
        if (root.TryGetProperty("schema", out var schemaElement) &&
            schemaElement.TryGetInt32(out var schemaValue) && schemaValue != 1)
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (sha.Length != 64 || !sha.All(IsHex))
            return null;

        long size = 0;
        if (root.TryGetProperty("size", out var sizeElement) &&
            sizeElement.ValueKind == JsonValueKind.Number)
            sizeElement.TryGetInt64(out size);

        return new PluginManifest
        {
            Schema = root.TryGetProperty("schema", out var schema) && schema.TryGetInt32(out var s) ? s : 1,
            Name = Str(root, "name") ?? "BD2SkinUnlock",
            Version = version,
            MinAppVersion = Str(root, "minAppVersion") ?? "",
            Url = url,
            Sha256 = sha.ToLowerInvariant(),
            Size = size,
            Notes = Str(root, "notes"),
        };
    }

    /// <summary>下载并校验插件 DLL；成功后放到 pending 目录，等待 Apply。</summary>
    public static async Task<string> DownloadAsync(
        PluginManifest manifest,
        StateStore store,
        CancellationToken cancellationToken = default)
    {
        var dir = PendingDir(store, manifest.Version);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, PluginFileName);

        if (File.Exists(target) && HashMatches(target, manifest))
            return target;

        var tmp = target + ".download";
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
        request.Headers.UserAgent.ParseAdd("BD2ModManager/plugin-update");
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"插件下载返回 HTTP {(int)response.StatusCode}");

        if (response.Content.Headers.ContentLength is long length && length > MaxDownloadBytes)
            throw new InvalidOperationException("插件文件超过 32 MB，已拒绝下载");

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                    throw new InvalidOperationException("插件文件超过 32 MB，已拒绝下载");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        try
        {
            var actual = Hashing.Sha256File(tmp);
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("插件 SHA256 校验失败，已丢弃下载文件");
            if (manifest.Size > 0 && new FileInfo(tmp).Length != manifest.Size)
                throw new InvalidOperationException("插件大小与清单不一致，已丢弃下载文件");
            File.Move(tmp, target, overwrite: true);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return target;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>把已下载的插件替换进已安装组件；游戏运行中则保留 pending 并返回 false。</summary>
    public static PluginApplyOutcome Apply(
        GameInstall game,
        ComponentManager components,
        StateStore store,
        PluginManifest manifest,
        string downloadedDll)
    {
        if (ProcessGuard.IsGameRunning())
            return new PluginApplyOutcome(false, "游戏正在运行，插件已下载，退出游戏后再次点击安装即可。", true);

        var r = components.ReplaceFile(
            game, "BD2SkinUnlock", PluginFileName, downloadedDll, $"插件更新 {manifest.Version}");
        if (!r.Success)
            return new PluginApplyOutcome(false, string.Join("；", r.Errors), true);

        try
        {
            Directory.Delete(PendingDir(store, manifest.Version), recursive: true);
        }
        catch { }

        return new PluginApplyOutcome(true, $"插件已更新到 {manifest.Version}，重启游戏后生效。", false);
    }

    public static bool TryGetPending(StateStore store, out PluginManifest manifest, out string dllPath)
    {
        manifest = new PluginManifest();
        dllPath = "";
        var root = Path.Combine(store.BaseDir, "plugin-update");
        if (!Directory.Exists(root)) return false;

        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var mf = Path.Combine(dir, "manifest.json");
            var dll = Path.Combine(dir, PluginFileName);
            if (!File.Exists(mf) || !File.Exists(dll)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(mf));
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.Version)) continue;
                if (!HashMatches(dll, parsed)) continue;
                manifest = parsed;
                dllPath = dll;
                return true;
            }
            catch { }
        }
        return false;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b))
            return a > b;
        return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string PendingDir(StateStore store, string version) =>
        Path.Combine(store.BaseDir, "plugin-update", "pending-" + Safe(version));

    private static string Safe(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static bool HashMatches(string path, PluginManifest manifest)
    {
        try { return string.Equals(Hashing.Sha256File(path), manifest.Sha256, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BD2ModManager/plugin-update");
        return client;
    }
}
