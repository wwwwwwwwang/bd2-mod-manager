using System.Net.Http;
using System.Text.Json;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

/// <summary>
/// 轻量更新检查：只读 GitHub Release 元数据，不下载、不执行任何文件。
/// 每次启动都请求一次；网络失败时回退到最近一次成功结果。
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/wwwwwwwwang/bd2-mod-manager/releases/latest";

    private const string ReleasesPage =
        "https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var cache = ReadCache();

        // 不按 24 小时阻断检查：新版本可能在上一次检查后立刻发布。
        // 缓存只作为网络失败时的回退，不作为“已检查”的结论。
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("BD2ModManager/update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return cache != null
                    ? FromCache(cache, currentVersion, $"GitHub API 返回 {(int)response.StatusCode}")
                    : UpdateCheckResult.Failed($"GitHub API 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var url = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(tag))
                return UpdateCheckResult.Failed("GitHub Release 没有版本号");

            var latest = NormalizeVersion(tag);
            if (latest == null)
                return UpdateCheckResult.Failed($"无法识别版本号：{tag}");

            var assets = FindAssets(root, latest);
            var result = UpdateCheckResult.CreateSuccess(
                currentVersion,
                latest,
                string.IsNullOrWhiteSpace(url) ? ReleasesPage : url!,
                assets.DownloadUrl,
                assets.ChecksumUrl,
                assets.AssetName);

            WriteCache(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("检查超时");
        }
        catch (Exception ex)
        {
            return cache != null
                ? FromCache(cache, currentVersion, ex.Message)
                : UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 从 Release 资产中找当前平台的单文件 exe 和校验和。找不到时返回空，
    /// 调用方会退化成“手动下载”，不影响版本提示。
    /// </summary>
    private static (string? DownloadUrl, string? ChecksumUrl, string? AssetName) FindAssets(
        JsonElement root,
        string latestVersion)
    {
        string? downloadUrl = null;
        string? checksumUrl = null;
        string? assetName = null;
        var tagName = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            var fallback = BuildFallbackAssets(latestVersion, tagName);
            return (fallback.DownloadUrl, fallback.ChecksumUrl, fallback.AssetName);
        }

        var expected = $"BD2ModManager-{latestVersion}-win-x64.exe";
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement) ||
                !asset.TryGetProperty("browser_download_url", out var urlElement))
                continue;

            var name = nameElement.GetString();
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

            if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
                continue;
            }

            if (!name.StartsWith("BD2ModManager-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            // 优先精确匹配当前 tag；预发布 tag 去掉后缀后，退而取第一个同平台资产。
            if (downloadUrl == null || string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = url;
                assetName = name;
            }
        }

        if (downloadUrl == null || checksumUrl == null)
        {
            var fallback = BuildFallbackAssets(latestVersion, tagName);
            downloadUrl ??= fallback.DownloadUrl;
            checksumUrl ??= fallback.ChecksumUrl;
            assetName ??= fallback.AssetName;
        }

        return (downloadUrl, checksumUrl, assetName);
    }

    /// <summary>
    /// Release 资产列表偶尔拿不到（例如 API 限流后的旧缓存），按仓库固定的
    /// Release 命名规则拼出下载地址；下载时仍会用 SHA256SUMS 校验。
    /// </summary>
    private static (string DownloadUrl, string ChecksumUrl, string AssetName) BuildFallbackAssets(
        string latestVersion,
        string? tagName = null)
    {
        var tag = string.IsNullOrWhiteSpace(tagName) ? $"v{latestVersion}" : tagName!.Trim();
        if (!tag.StartsWith('v') && !tag.StartsWith('V')) tag = "v" + tag;
        var baseUrl = $"https://github.com/wwwwwwwwang/bd2-mod-manager/releases/download/{tag}";
        var assetName = $"BD2ModManager-{latestVersion}-win-x64.exe";
        return ($"{baseUrl}/{assetName}", $"{baseUrl}/SHA256SUMS.txt", assetName);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BD2ModManager/update-check");
        return client;
    }

    private static UpdateCheckResult FromCache(
        UpdateCache cache,
        string currentVersion,
        string? warning = null)
    {
        var fallback = BuildFallbackAssets(cache.LatestVersion);
        var result = UpdateCheckResult.CreateSuccess(
            currentVersion,
            cache.LatestVersion,
            cache.ReleaseUrl,
            string.IsNullOrWhiteSpace(cache.DownloadUrl) ? fallback.DownloadUrl : cache.DownloadUrl,
            string.IsNullOrWhiteSpace(cache.ChecksumUrl) ? fallback.ChecksumUrl : cache.ChecksumUrl,
            string.IsNullOrWhiteSpace(cache.AssetName) ? fallback.AssetName : cache.AssetName);
        return warning == null ? result : result with { Warning = warning };
    }

    private static UpdateCache? ReadCache()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(UpdateCheckResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var cache = new UpdateCache
            {
                CheckedAt = DateTimeOffset.UtcNow,
                LatestVersion = result.LatestVersion,
                ReleaseUrl = result.ReleaseUrl,
                DownloadUrl = result.DownloadUrl,
                ChecksumUrl = result.ChecksumUrl,
                AssetName = result.AssetName
            };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // 更新检查不能影响主功能；缓存失败就下次重新查。
        }
    }

    private static string CachePath =>
        Path.Combine(new StateStore().BaseDir, "update-check.json");

    private static string? NormalizeVersion(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        if (!Version.TryParse(value, out var version)) return null;
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }

    private sealed class UpdateCache
    {
        public DateTimeOffset CheckedAt { get; set; }
        public string LatestVersion { get; set; } = "";
        public string ReleaseUrl { get; set; } = ReleasesPage;
        public string? DownloadUrl { get; set; }
        public string? ChecksumUrl { get; set; }
        public string? AssetName { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool Success,
    bool Available,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? Error = null,
    string? Warning = null,
    string? DownloadUrl = null,
    string? ChecksumUrl = null,
    string? AssetName = null)
{
    /// <summary>Release 同时提供了 exe 和 SHA256SUMS 时才走自动更新。</summary>
    public bool CanAutoUpdate =>
        Available &&
        !string.IsNullOrWhiteSpace(DownloadUrl) &&
        !string.IsNullOrWhiteSpace(ChecksumUrl) &&
        !string.IsNullOrWhiteSpace(AssetName);

    public static UpdateCheckResult CreateSuccess(
        string currentVersion,
        string latestVersion,
        string releaseUrl,
        string? downloadUrl = null,
        string? checksumUrl = null,
        string? assetName = null)
    {
        var current = ParseVersion(currentVersion);
        var latest = ParseVersion(latestVersion);
        return new UpdateCheckResult(
            true,
            current != null && latest != null && latest.CompareTo(current) > 0,
            currentVersion,
            latestVersion,
            releaseUrl,
            DownloadUrl: downloadUrl,
            ChecksumUrl: checksumUrl,
            AssetName: assetName);
    }

    public static UpdateCheckResult Failed(string error) =>
        new(false, false, "", "", "", error);

    private static Version? ParseVersion(string raw)
    {
        var value = raw.Trim().TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        return Version.TryParse(value, out var version) ? version : null;
    }
}
