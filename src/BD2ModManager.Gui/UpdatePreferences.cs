using System.Text.Json;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

/// <summary>
/// 自动更新相关的用户偏好。和游戏安装状态分开保存，
/// 换游戏目录也不会丢掉“更新包下载到哪里”的选择。
/// </summary>
internal sealed class UpdatePreferences
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string? DownloadDirectory { get; set; }

    private static string SettingsPath =>
        Path.Combine(new StateStore().BaseDir, "update-settings.json");

    public static string LoadDownloadDirectory()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return "";
            var settings = JsonSerializer.Deserialize<UpdatePreferences>(
                File.ReadAllText(path), JsonOpts);
            return settings?.DownloadDirectory?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static void SaveDownloadDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var settings = new UpdatePreferences
            {
                DownloadDirectory = Path.GetFullPath(directory)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch
        {
            // 记不住目录不影响本次更新；下次再让用户选一次即可。
        }
    }
}
