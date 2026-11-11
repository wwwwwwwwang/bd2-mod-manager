using System;
using System.IO;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 读取 GUI 写入的 modules.ini。插件不直接弹窗，也不改用户的 BepInEx 配置；
    /// GUI 是唯一的开关面板，插件只负责执行。
    /// </summary>
    internal static class ModuleFlags
    {
        internal static bool SkinUnlock = true;
        internal static bool Interactions = true;
        internal static bool DisableCensorship = true;
        internal static bool CovenantExperimental;
        internal static bool AllCostumes;
        internal static bool AllCharacters;
        internal static bool AllDatingCostumes;

        private static string Path =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BD2ModManager", "modules.ini");

        internal static void Reload()
        {
            try
            {
                var path = Path;
                if (!File.Exists(path)) return;

                var section = "";
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = line.Substring(eq + 1).Trim();

                    if (section != "modules") continue;
                    switch (key)
                    {
                        case "skinunlock": SkinUnlock = Parse(value, true); break;
                        case "interactions": Interactions = Parse(value, true); break;
                        case "disablecensorship": DisableCensorship = Parse(value, true); break;
                        case "covenantexperimental": CovenantExperimental = Parse(value, false); break;
                        case "allcostumes": AllCostumes = Parse(value, false); break;
                        case "allcharacters": AllCharacters = Parse(value, false); break;
                        case "alldatingcostumes": AllDatingCostumes = Parse(value, false); break;
                    }
                }
            }
            catch
            {
                // 读不到就保持上一次的值；插件不能因为配置文件坏了而不工作。
            }
        }

        private static bool Parse(string text, bool fallback)
        {
            if (bool.TryParse(text, out var value)) return value;
            if (text == "1") return true;
            if (text == "0") return false;
            return fallback;
        }
    }
}
