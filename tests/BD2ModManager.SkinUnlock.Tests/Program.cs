using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FakeGame;
using HarmonyLib;
using Proto.Net;
using Suiren.BD2.SkinUnlock;
using SkinPatches = Suiren.BD2.SkinUnlock.Patches;

// ---------------------------------------------------------------------------
// 离线验证：用假游戏结构把插件的完整链路走一遍。
//
// 覆盖：反射解析 → 读设计表 → 注入皮肤条目 → 补全弹窗列表 → 拦截点击
//      → 服务器覆盖后回放 → 退出回滚 → 补丁撤销。
//
// 关于补丁：插件引用的是 loader 里那份 HarmonyX（面向 Unity Mono）。
// 在 .NET 8 上它的 AccessTools 初始化会失败，所以这里用 NuGet 的 Harmony
// 顶替，并把「挂载失败时插件不能崩」本身也作为一条断言。
// ---------------------------------------------------------------------------

internal static class Program
{
    private static int _failures;
    private static int _steps;

    private static int Main()
    {
        // 插件程序集绑的是 0Harmony 2.9（loader 里那版），测试宿主持有的是 NuGet 版
        AppDomain.CurrentDomain.AssemblyResolve += ResolveHarmony;
        return Run();
    }

    private static Assembly ResolveHarmony(object sender, ResolveEventArgs e)
    {
        try
        {
            if (new AssemblyName(e.Name).Name != "0Harmony") return null;

            var path = Path.Combine(AppContext.BaseDirectory, "0Harmony.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int Run()
    {
        Database.Reset();

        // 设计表：服装 1001 有三套皮肤，服装 1002 有一套
        Database.AddSkinRow(2001, 1001);
        Database.AddSkinRow(2002, 1001);
        Database.AddSkinRow(2003, 1001);
        Database.AddSkinRow(2004, 1002);

        // 玩家拥有这两件服装，但只买过 2001 这一套
        var costume1 = new CostumeDBInfo { Id = 1001, DesignId = 2001 };
        costume1.OwnedDesigns.Add(2001);
        var costume2 = new CostumeDBInfo { Id = 1002, DesignId = 0 };

        Database.CostumeDict[1001] = costume1;
        Database.CostumeDict[1002] = costume2;
        Database.SkinDict[2001] = new PrestigeSkinDBInfo { CostumeId = 1001, CostumeDesignId = 2001, IsSet = true };

        // ------------------------------------------------------------ 1. 反射

        Console.WriteLine("1. 反射解析");
        var refs = new GameRefs();
        if (!refs.TryResolve())
        {
            Fail("解析游戏内部结构", $"{refs.Step} / {refs.Problem}");
            Console.WriteLine($"\n失败 {_failures} 项，中止。");
            return 1;
        }
        Ok($"解析成功：{refs.Describe()}");

        Check(refs.CostumeId != null && refs.CostumeDesignId != null, "拿到 CostumeDBInfo.Id / DesignId");
        Check(refs.SkinIsSet != null, "拿到 PrestigeSkinDBInfo.IsSet");
        Check(refs.SetSkin != null, "拿到 SetSkin(int, bool, Action)");
        Check(refs.GetConnectedSkins != null, "拿到 GetConnectedPrestigeSkinDBInfos");

        // ------------------------------------------------------------ 2. 设计表

        Console.WriteLine("2. 皮肤目录");
        var catalog = new SkinCatalog();
        var logs = new List<string>();
        if (!catalog.TryBuild(refs, logs.Add, s => logs.Add("WARN " + s)))
        {
            Fail("构建设计表目录", string.Join(" / ", logs));
            return 1;
        }
        Eq(catalog.SkinCount, 4, "读到 4 套皮肤");
        Eq(catalog.CostumeCount, 2, "覆盖 2 件服装");

        // ------------------------------------------------------------ 3. 注入

        Console.WriteLine("3. 注入皮肤条目");
        var state = new SkinState();
        Eq(state.EnsureInjected(refs, catalog), 3, "补入 3 条（2002 / 2003 / 2004）");
        Eq(Database.SkinDict.Count, 4, "皮肤字典从 1 条变成 4 条");
        Eq(state.EnsureInjected(refs, catalog), 0, "再次调用不重复注入");
        Check(Database.SkinDict.ContainsKey(2001), "游戏原本的条目没被动过");

        var entry2004 = Database.SkinDict[2004];
        Check(entry2004.CostumeId == 1002 && entry2004.CostumeDesignId == 2004,
            "补入条目的 costumeId / designId 正确");

        // ------------------------------------------------------------ 4. 补丁

        Console.WriteLine("4. 补丁");

        SkinPatches.Active = true;
        SkinPatches.Refs = refs;
        SkinPatches.Catalog = catalog;
        SkinPatches.State = state;
        SkinPatches.LogInfo = s => Console.WriteLine("    log  " + s);
        SkinPatches.LogWarn = s => Console.WriteLine("    WARN " + s);
        SkinPatches.SaveSelection = () => { };

        var harmony = new Harmony("com.suiren.bd2.skinunlock.test");
        var mounted = SkinPatches.Install(harmony, refs);
        var realPatch = mounted == 2;

        // 无论挂没挂上，Install 都不能把持有它的插件带崩
        Check(mounted >= 0, "挂载过程没有抛异常穿过补丁层");

        if (realPatch)
        {
            Ok("两个补丁都挂到了真实方法上");
        }
        else
        {
            Console.WriteLine($"    注：当前环境挂上 {mounted}/2 个（HarmonyX 面向 Unity Mono，");
            Console.WriteLine("        .NET 8 下无法打补丁）。改为按 Harmony 的调用约定直接调用补丁方法。");
        }

        var postfixMethod = typeof(SkinPatches).GetMethod(
            "GetConnected_Postfix", BindingFlags.Static | BindingFlags.NonPublic);
        var prefixMethod = typeof(SkinPatches).GetMethod(
            "SetSkin_Prefix", BindingFlags.Static | BindingFlags.NonPublic);

        Check(postfixMethod != null && prefixMethod != null, "补丁方法本身存在");

        // 「游戏问这件服装有哪些皮肤」
        List<PrestigeSkinDBInfo> Connected(CostumeDBInfo costume)
        {
            var raw = costume.GetConnectedPrestigeSkinDBInfos();
            if (realPatch) return raw;

            var args = new object[] { costume, raw };
            postfixMethod.Invoke(null, args);
            return (List<PrestigeSkinDBInfo>)args[1];
        }

        // 「玩家点了某套皮肤」，返回原方法是否被跳过
        bool Click(int designId, bool isSet, Action callback)
        {
            if (realPatch)
            {
                Database.SetSkin(designId, isSet, callback);
                return Database.SetSkinCalls == 0;
            }

            // Prefix 返回 true 表示「继续执行原方法」，false 表示「拦下来」
            var runOriginal = (bool)prefixMethod.Invoke(null, new object[] { new object[] { designId, isSet, callback } });
            if (runOriginal) Database.SetSkin(designId, isSet, callback);
            return !runOriginal;
        }

        Eq(Connected(costume1).Count, 3, "服装 1001 的皮肤列表从 1 条补到 3 条");
        Eq(Connected(costume2).Count, 1, "服装 1002 原本没有皮肤，现在有 1 条");
        Eq(Connected(costume1).Count, 3, "重复查询不会重复追加");

        // ------------------------------------------------------------ 5. 点击

        Console.WriteLine("5. 点击皮肤");
        Database.SetSkinCalls = 0;
        var callbackFired = false;

        var skipped = Click(2002, true, () => callbackFired = true);

        Check(skipped, "原方法被跳过（没有向服务器发切换）");
        Check(callbackFired, "界面回调被正常触发");
        Eq(costume1.DesignId, 2002, "服装当前皮肤改成了 2002");
        Check(state.Selection.TryGetValue(1001, out var saved) && saved == 2002, "选择被记入选择表");

        var isSetCount = 0;
        foreach (var skin in Database.SkinDict.Values)
        {
            if (skin.CostumeId == 1001 && skin.IsSet) isSetCount++;
        }
        Eq(isSetCount, 1, "同一件服装下只有一套皮肤处于选中态");

        // ------------------------------------------------------------ 6. 回放

        Console.WriteLine("6. 服务器同步把皮肤冲掉后");
        costume1.DesignId = 0;                      // 模拟同步下发的旧值
        Eq(state.ReapplySelection(refs), 1, "回放 1 件服装的选择");
        Eq(costume1.DesignId, 2002, "选择被写回 2002");
        Eq(state.ReapplySelection(refs), 0, "值已经对了就不再写");

        // ------------------------------------------------------------ 7. 取消

        Console.WriteLine("7. 取消选中");
        Database.SetSkinCalls = 0;
        Click(2002, false, null);
        Eq(Database.SetSkinCalls, 0, "取消时不发请求");
        Eq(costume1.DesignId, 0, "服装回到默认皮肤");
        Check(!state.Selection.ContainsKey(1001), "选择记录被清掉");

        // ------------------------------------------------------------ 8. 回滚

        Console.WriteLine("8. 退出回滚");
        state.Rollback(refs);
        Eq(Database.SkinDict.Count, 1, "注入的 3 条全部移除");
        Check(Database.SkinDict.ContainsKey(2001), "游戏原本的条目还在");

        // ------------------------------------------------------------ 9. 撤销

        Console.WriteLine("9. 撤销补丁");
        SkinPatches.Active = false;
        SkinPatches.Uninstall(harmony);
        Eq(costume1.GetConnectedPrestigeSkinDBInfos().Count, 1, "皮肤列表回到游戏原样");

        // ------------------------------------------------------------ 结果

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine($"全部通过：{_steps} 项检查。");
            return 0;
        }

        Console.WriteLine($"{_steps} 项检查中有 {_failures} 项失败。");
        return 1;
    }

    // ---------------------------------------------------------------- 断言

    private static void Ok(string what)
    {
        _steps++;
        Console.WriteLine($"  [OK] {what}");
    }

    private static void Fail(string what, string detail = null)
    {
        _failures++;
        Console.WriteLine($"  [FAIL] {what}" + (detail == null ? "" : $" — {detail}"));
    }

    private static void Check(bool condition, string what, string detail = null)
    {
        if (condition) Ok(what);
        else Fail(what, detail);
    }

    private static void Eq(int actual, int expected, string what)
        => Check(actual == expected, what, expected == actual ? null : $"期望 {expected}，实际 {actual}");
}
