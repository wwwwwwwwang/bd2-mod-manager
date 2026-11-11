using System.Globalization;
using System.Text;

namespace BD2ModManager.Core;

/// <summary>
/// 用户可见的功能模块开关。放在状态目录而不是游戏目录里，
/// 插件和 GUI 读同一份文件，避免两边各存一套。
/// </summary>
public sealed class ModuleSettings
{
    public const int CurrentVersion = 1;

    public bool DisclaimerAccepted { get; set; }
    public int DisclaimerVersion { get; set; }

    // 低风险：默认开启
    public bool SkinUnlock { get; set; } = true;
    public bool Interactions { get; set; } = true;
    public bool DisableCensorship { get; set; } = true;
    public bool Diagnostics { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;

    // 高风险：默认关闭，开启需要二次确认
    public bool CovenantExperimental { get; set; }
    public bool AllCostumes { get; set; }
    public bool AllCharacters { get; set; }
    public bool AllDatingCostumes { get; set; }

    public string Path { get; private set; } = "";

    public static string PathFor(StateStore store) =>
        System.IO.Path.Combine(store.BaseDir, "modules.ini");

    public static ModuleSettings Load(StateStore store)
    {
        var result = new ModuleSettings { Path = PathFor(store) };
        try
        {
            if (!File.Exists(result.Path)) return result;

            var section = "";
            foreach (var raw in File.ReadAllLines(result.Path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim().ToLowerInvariant();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var value = line[(eq + 1)..].Trim();

                if (section == "general")
                {
                    if (key == "disclaimeraccepted") result.DisclaimerAccepted = ParseBool(value, false);
                    else if (key == "disclaimerversion") result.DisclaimerVersion = ParseInt(value, 0);
                }
                else if (section == "modules")
                {
                    switch (key)
                    {
                        case "skinunlock": result.SkinUnlock = ParseBool(value, true); break;
                        case "interactions": result.Interactions = ParseBool(value, true); break;
                        case "disablecensorship": result.DisableCensorship = ParseBool(value, true); break;
                        case "covenantexperimental": result.CovenantExperimental = ParseBool(value, false); break;
                        case "allcostumes": result.AllCostumes = ParseBool(value, false); break;
                        case "allcharacters": result.AllCharacters = ParseBool(value, false); break;
                        case "alldatingcostumes": result.AllDatingCostumes = ParseBool(value, false); break;
                        case "diagnostics": result.Diagnostics = ParseBool(value, true); break;
                        case "autoupdate": result.AutoUpdate = ParseBool(value, true); break;
                    }
                }
            }
        }
        catch
        {
            // 配置坏了就按默认值启动，不能因此让程序打不开。
        }

        return result;
    }

    public void Save(StateStore store)
    {
        Path = PathFor(store);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var sb = new StringBuilder();
        sb.AppendLine("[general]");
        sb.AppendLine("DisclaimerAccepted=" + (DisclaimerAccepted ? "true" : "false"));
        sb.AppendLine("DisclaimerVersion=" + DisclaimerVersion.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("[modules]");
        sb.AppendLine("SkinUnlock=" + Bool(SkinUnlock));
        sb.AppendLine("Interactions=" + Bool(Interactions));
        sb.AppendLine("DisableCensorship=" + Bool(DisableCensorship));
        sb.AppendLine("CovenantExperimental=" + Bool(CovenantExperimental));
        sb.AppendLine("AllCostumes=" + Bool(AllCostumes));
        sb.AppendLine("AllCharacters=" + Bool(AllCharacters));
        sb.AppendLine("AllDatingCostumes=" + Bool(AllDatingCostumes));
        sb.AppendLine("Diagnostics=" + Bool(Diagnostics));
        sb.AppendLine("AutoUpdate=" + Bool(AutoUpdate));

        var temp = Path + ".tmp";
        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    public bool HasRiskyModulesEnabled =>
        CovenantExperimental || AllCostumes || AllCharacters || AllDatingCostumes;

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool ParseBool(string text, bool fallback)
    {
        if (bool.TryParse(text, out var value)) return value;
        if (text == "1") return true;
        if (text == "0") return false;
        return fallback;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
