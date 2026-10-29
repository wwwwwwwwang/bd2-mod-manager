using System.Net.Http;
using System.Text.Json;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

/// <summary>
/// 轻量更新检查：只读 GitHub Release 元数据，不下载、不执行任何文件。
/// 结果缓存 24 小时；手动检查时绕过缓存。
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
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var cache = ReadCache();

        if (!force && cache != null &&
            DateTimeOffset.UtcNow - cache.CheckedAt < TimeSpan.FromHours(24))
        {
            return FromCache(cache, currentVersion);
        }

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

            var result = UpdateCheckResult.CreateSuccess(
                currentVersion,
                latest,
                string.IsNullOrWhiteSpace(url) ? ReleasesPage : url!);

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
        var result = UpdateCheckResult.CreateSuccess(
            currentVersion,
            cache.LatestVersion,
            cache.ReleaseUrl);
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
                ReleaseUrl = result.ReleaseUrl
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
    }
}

public sealed record UpdateCheckResult(
    bool Success,
    bool Available,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? Error = null,
    string? Warning = null)
{
    public static UpdateCheckResult CreateSuccess(
        string currentVersion,
        string latestVersion,
        string releaseUrl)
    {
        var current = ParseVersion(currentVersion);
        var latest = ParseVersion(latestVersion);
        return new UpdateCheckResult(
            true,
            current != null && latest != null && latest.CompareTo(current) > 0,
            currentVersion,
            latestVersion,
            releaseUrl);
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
