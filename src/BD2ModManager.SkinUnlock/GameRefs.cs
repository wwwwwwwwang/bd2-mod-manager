using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>设计表查询的三种结果。查询抛异常时是「不可用」，绝不能当成「缺行」。</summary>
    internal enum TableRowStatus
    {
        Found,
        Missing,
        Unavailable,
    }

    /// <summary>
    /// 游戏内部结构的反射视图。
    ///
    /// 设计要点：解析一次、缓存全部成员，之后所有热路径只走缓存。
    /// 商品插件每次都重新遍历「所有程序集 → 所有类型 → 所有静态字段」来找字典，
    /// 这里改成「按目标类型反查一次 + 结果缓存 + 失败后限次重试」。
    /// </summary>
    internal sealed class GameRefs
    {
        // ---- 我拥有的服装 ----
        internal Type CostumeType;
        internal PropertyInfo CostumeId;          // costumeId
        internal PropertyInfo CostumeDesignId;    // 当前穿的 designId
        internal FieldInfo CostumeDict;           // Dictionary<long, CostumeDBInfo>

        // ---- 救赎之塔（EvilCastle Roguelike）的独立服装镜像 ----
        // 塔内不用上面那本全局字典，而是每局由服务器响应重建的一份独立字典。
        // 只有把选择同步进去，塔里的立绘/皮肤名才会生效。
        internal List<FieldInfo> TowerCostumeDicts = new List<FieldInfo>();
        internal List<MethodInfo> TowerCostumeAdds = new List<MethodInfo>();
        internal string TowerProblem = "";

        // ---- 皮肤库 ----
        internal Type SkinType;
        internal PropertyInfo SkinCostumeId;      // designId → 属于哪个 costumeId
        internal PropertyInfo SkinDesignId;
        internal PropertyInfo SkinIsSet;          // 是否被选中
        internal FieldInfo SkinDict;              // Dictionary<int, PrestigeSkinDBInfo>
        internal MethodInfo SetSkin;              // static (int designId, bool isSet, Action cb)
        internal MethodInfo AddSkin;              // static (PrestigeSkinDBInfo, bool)

        // ---- 皮肤设计表 ----
        internal Type TableType;                  // Proto.Design.common.PrestigeSkinTable
        internal PropertyInfo TableId;            // designId
        internal PropertyInfo TableCostumeId;
        internal MethodInfo GetTableRow;          // static (int) → TableType
        internal MethodInfo GetTableIds;          // RawDataManager.GetTableOfIds(enum, string)
        internal object DbCommon;
        internal Type CostumeTableType;           // Proto.Design.common.CostumeTable
        private object _rdmInstance;              // RawDataManager 单例
        private MethodInfo _getValueObject;       // RawDataManager.GetValueObject<T>(enum, string, int)
        private Type _getValueRowType;

        // ---- 服装上「查这个服装有哪些皮肤」 ----
        internal MethodInfo GetConnectedSkins;    // instance → List<PrestigeSkinDBInfo>

        // ---- 皮肤弹窗（贴图回调兜底）----
        internal Type PopupType;                  // SkinExchangePopupUI
        internal MethodInfo LoadTexture;          // (int designId, ref Action<Sprite> cb)

        // ---- 大厅互动场景 ----
        internal Type LobbyType;                  // Proto.Design.common.LobbySettingItemTable
        internal PropertyInfo LobbyId;
        internal PropertyInfo LobbySkinId;        // PrestigeSkinId
        internal PropertyInfo LobbyKind;          // Type（仅用于日志）
        internal PropertyInfo LobbyInteractionId; // InteractionId（>0 表示互动场景）
        internal PropertyInfo LobbyCostumeId;     // CostumeId（仅用于日志）
        internal MethodInfo GetAllLobbyItems;     // static () → List<LobbySettingItemTable>

        internal Type ItemType;                   // Proto.Net.ItemDBInfo
        internal PropertyInfo ItemId;
        internal PropertyInfo ItemIsNew;
        internal PropertyInfo ItemKind;           // Type，注入时固定写 25
        internal PropertyInfo ItemInvenIndex;
        internal PropertyInfo ItemCount;
        internal PropertyInfo ItemTimeValue;
        internal PropertyInfo ItemKeepFlag;
        internal FieldInfo ItemDict;              // Dictionary<int, ItemDBInfo>

        // ---- 额外解锁目标（全部为可选，缺失不影响皮肤主功能）----
        internal MethodInfo InteractionUnlockCheck;   // static bool CheckInteractionUnlock(...)
        internal Type CensorshipType;
        internal MethodInfo CensorshipCheck;          // static bool IsCensorship(...)
        internal Type SpineType;                      // SpineInteractionUI
        internal MethodInfo SpineSetUI;               // void SetUI(..., List<int>)
        internal readonly List<PropertyInfo> SpineGateGetters = new List<PropertyInfo>();
        internal readonly List<MethodInfo> SpineRestoreTargets = new List<MethodInfo>(); // void Xxx(int, int)
        internal Type DatingFriendsType;
        internal readonly List<MethodInfo> DatingPredicates = new List<MethodInfo>();
        internal Type UserDbType;
        internal PropertyInfo PortraitCostumeId;      // UserDBInfo.UsePortraitCostumeId
        internal PropertyInfo PortraitDesignId;       // UserDBInfo.UsePortraitCostumeDesignTableID
        internal string ExtraProblem = "";

        internal bool Ready;
        internal string Problem = "";

        /// <summary>大厅互动场景那条链的缺失说明。为空表示齐全。</summary>
        internal string InteractionProblem = "";

        /// <summary>解析进度，用于日志定位到底卡在哪一步。</summary>
        internal string Step = "";

        private static readonly string[] SystemPrefixes =
        {
            "System", "Microsoft", "mscorlib", "netstandard", "Mono.", "UnityEngine",
            "Unity.", "BepInEx", "0Harmony", "HarmonyX", "Iced", "Newtonsoft", "Cecil",
        };

        /// <summary>尝试解析。已成功则直接返回 true；失败不抛异常，原因写在 Problem。</summary>
        internal bool TryResolve()
        {
            if (Ready) return true;

            try
            {
                Step = "CostumeDBInfo";
                CostumeType = FindType("Proto.Net.CostumeDBInfo", "CostumeDBInfo");
                if (CostumeType == null) return Fail("找不到类型 Proto.Net.CostumeDBInfo");

                CostumeId = CostumeType.GetProperty("Id");
                CostumeDesignId = CostumeType.GetProperty("DesignId");
                if (CostumeId == null || CostumeDesignId == null)
                    return Fail("CostumeDBInfo 上没有 Id / DesignId 属性");

                Step = "服装字典";
                CostumeDict = FindStaticDict(CostumeType);
                if (CostumeDict == null) return Fail("找不到存放 CostumeDBInfo 的静态字典");

                // 塔字典属于锦上添花：找不到时主功能照常工作
                Step = "救赎之塔服装字典";
                ResolveTower();

                Step = "PrestigeSkinDBInfo";
                SkinType = FindType("Proto.Net.PrestigeSkinDBInfo", "PrestigeSkinDBInfo");
                if (SkinType == null) return Fail("找不到类型 Proto.Net.PrestigeSkinDBInfo");

                SkinCostumeId = SkinType.GetProperty("CostumeId");
                SkinDesignId = SkinType.GetProperty("CostumeDesignId");
                SkinIsSet = SkinType.GetProperty("IsSet");
                if (SkinCostumeId == null || SkinDesignId == null || SkinIsSet == null)
                    return Fail("PrestigeSkinDBInfo 上缺少 CostumeId / CostumeDesignId / IsSet");

                Step = "皮肤字典";
                SkinDict = FindStaticDict(SkinType);
                if (SkinDict == null) return Fail("找不到存放 PrestigeSkinDBInfo 的静态字典");

                Step = "PrestigeSkinTable";
                TableType = FindType("Proto.Design.common.PrestigeSkinTable", "PrestigeSkinTable");
                if (TableType == null) return Fail("找不到类型 PrestigeSkinTable");

                TableId = TableType.GetProperty("Id");
                TableCostumeId = TableType.GetProperty("CostumeId");
                if (TableId == null || TableCostumeId == null)
                    return Fail("PrestigeSkinTable 上缺少 Id / CostumeId");

                // 全服装镜像要读完整的 CostumeTable；找不到时只影响高风险的「全服装」，
                // 不影响皮肤主功能，所以这里不 Fail。
                CostumeTableType = FindType("Proto.Design.common.CostumeTable", "CostumeTable");

                Step = "皮肤表读取器";
                GetTableRow = FindTableRowGetter();
                if (GetTableRow == null) return Fail("找不到 PrestigeSkinTable 的按 Id 取行方法");

                Step = "RawDataManager";
                if (!ResolveRawDataManager())
                    return Fail("找不到 RawDataManager.GetTableOfIds");

                Step = "GetConnectedPrestigeSkinDBInfos";
                GetConnectedSkins = CostumeType.GetMethod(
                    "GetConnectedPrestigeSkinDBInfos",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                Step = "SetSkin";
                SetSkin = FindSetSkin();

                Step = "AddPrestigeSkinDBInfo";
                AddSkin = FindAddSkin();

                // 下面两条是「锦上添花」：失败也不影响主功能，只记原因
                Step = "皮肤弹窗";
                ResolvePopup();

                Step = "大厅互动场景";
                ResolveInteraction();

                Ready = true;
                Problem = "";
                return true;
            }
            catch (Exception e)
            {
                return Fail(Step + " 阶段异常：" + e.Message);
            }
        }

        private bool Fail(string why)
        {
            Problem = why;
            return false;
        }

        // ---------------------------------------------------------------- 查找

        /// <summary>先按全名找，再按短名兜底（游戏改命名空间时不至于直接失效）。</summary>
        internal static Type FindType(string fullName, string shortName = null)
        {
            foreach (var asm in SafeAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(shortName)) return null;

            foreach (var asm in SafeAssemblies())
            {
                foreach (var t in SafeTypes(asm))
                {
                    if (t != null && t.Name == shortName) return t;
                }
            }
            return null;
        }

        /// <summary>
        /// 救赎之塔的服装镜像。塔管理类上同时有：
        ///   Dictionary&lt;long, CostumeDBInfo&gt;（按 InvenIndex 索引）
        ///   Dictionary&lt;int, List&lt;CostumeDBInfo&gt;&gt;（按服装 Id 分组）
        /// 普通模式的全局字典没有第二个字段，因此可以安全区分。
        /// 解析失败只影响塔内效果，不阻断主功能。
        /// </summary>
        private void ResolveTower()
        {
            TowerCostumeDicts.Clear();
            TowerCostumeAdds.Clear();
            TowerProblem = "";

            var listType = typeof(List<>).MakeGenericType(CostumeType);

            foreach (var asm in SafeAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (StartsWithAny(asmName, SystemPrefixes)) continue;

                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;

                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    var mains = new List<FieldInfo>();
                    var hasIndex = false;

                    foreach (var f in fields)
                    {
                        try
                        {
                            if (!typeof(IDictionary).IsAssignableFrom(f.FieldType)) continue;
                            if (!f.FieldType.IsGenericType) continue;

                            var args = f.FieldType.GetGenericArguments();
                            if (args.Length != 2) continue;

                            if (args[0] == typeof(long) && args[1] == CostumeType && f != CostumeDict)
                                mains.Add(f);
                            else if (args[0] == typeof(int) && args[1] == listType)
                                hasIndex = true;
                        }
                        catch { }
                    }

                    if (mains.Count == 0 || !hasIndex) continue;

                    foreach (var f in mains)
                    {
                        try
                        {
                            if (f.GetValue(null) is IDictionary && !TowerCostumeDicts.Contains(f))
                                TowerCostumeDicts.Add(f);
                        }
                        catch { }
                    }

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        try
                        {
                            if (m.ReturnType != typeof(void)) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1 || ps[0].ParameterType != CostumeType) continue;
                            if (!TowerCostumeAdds.Contains(m)) TowerCostumeAdds.Add(m);
                        }
                        catch { }
                    }
                }
            }

            if (TowerCostumeDicts.Count == 0)
                TowerProblem = "找不到救赎之塔的独立服装字典";
            else if (TowerCostumeAdds.Count == 0)
                TowerProblem = "找到塔服装字典，但没有找到插入方法";
        }

        /// <summary>
        /// 找「值类型是 valueType（或其派生）」的静态字典字段。
        /// 额外要求字典实例非空，避免抓到同名的空壳字段。
        /// </summary>
        private static FieldInfo FindStaticDict(Type valueType)
        {
            FieldInfo fallback = null;

            foreach (var asm in SafeAssemblies())
            {
                var name = asm.GetName().Name ?? "";
                if (StartsWithAny(name, SystemPrefixes)) continue;

                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;

                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var f in fields)
                    {
                        try
                        {
                            if (!typeof(IDictionary).IsAssignableFrom(f.FieldType)) continue;

                            var vt = ValueTypeOf(f.FieldType);
                            if (vt == null || !valueType.IsAssignableFrom(vt)) continue;

                            var dict = f.GetValue(null) as IDictionary;
                            if (dict != null && dict.Count > 0) return f;

                            if (fallback == null) fallback = f;
                        }
                        catch { }
                    }
                }
            }

            // 字典暂时为空也认，运行期会等它填满
            return fallback;
        }

        private static Type ValueTypeOf(Type dictType)
        {
            if (!dictType.IsGenericType) return null;
            var args = dictType.GetGenericArguments();
            return args.Length == 2 ? args[1] : null;
        }

        /// <summary>找「静态、只吃一个 int、返回 PrestigeSkinTable」的方法。</summary>
        private MethodInfo FindTableRowGetter()
        {
            foreach (var asm in SafeAssemblies())
            {
                var name = asm.GetName().Name ?? "";
                if (StartsWithAny(name, SystemPrefixes)) continue;

                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        try
                        {
                            if (m.ReturnType != TableType) continue;
                            var ps = m.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) return m;
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        private bool ResolveRawDataManager()
        {
            var rdm = FindType("RawDataManager", "RawDataManager");
            if (rdm == null) return false;

            MethodInfo[] methods;
            try { methods = rdm.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return false; }

            foreach (var m in methods)
            {
                try
                {
                    var ps = m.GetParameters();
                    if (m.Name != "GetTableOfIds" || ps.Length != 2) continue;
                    if (!ps[0].ParameterType.IsEnum || ps[1].ParameterType != typeof(string)) continue;
                    if (!typeof(IList).IsAssignableFrom(m.ReturnType)) continue;

                    GetTableIds = m;
                    DbCommon = Enum.Parse(ps[0].ParameterType, "DB_COMMON");
                    return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>SetSkin：静态 (int, bool, Action)，且必须落在持有皮肤字典的那个类上。</summary>
        private MethodInfo FindSetSkin()
        {
            var host = SkinDict?.DeclaringType;
            if (host == null) return null;

            MethodInfo loose = null;

            foreach (var m in host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[0].ParameterType != typeof(int)) continue;
                    if (ps[1].ParameterType != typeof(bool)) continue;
                    if (!typeof(Delegate).IsAssignableFrom(ps[2].ParameterType)) continue;

                    if (m.Name == "SetSkin") return m;
                    if (loose == null) loose = m;
                }
                catch { }
            }
            return loose;
        }

        private MethodInfo FindAddSkin()
        {
            var host = SkinDict?.DeclaringType;
            if (host == null) return null;

            foreach (var m in host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    if (m.ReturnType != typeof(void)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == SkinType && ps[1].ParameterType == typeof(bool))
                        return m;
                }
                catch { }
            }
            return null;
        }

        // ---------------------------------------------------------------- 弹窗 / 互动场景

        /// <summary>皮肤弹窗的 <c>LoadTexture(int, ref Action&lt;Sprite&gt;)</c>。找不到就跳过贴图兜底。</summary>
        private void ResolvePopup()
        {
            PopupType = FindType("SkinExchangePopupUI", "SkinExchangePopupUI");
            if (PopupType == null) return;

            MethodInfo[] methods;
            try
            {
                methods = PopupType.GetMethods(
                    BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch { return; }

            foreach (var m in methods)
            {
                if (m.Name != "LoadTexture") continue;

                ParameterInfo[] ps;
                try { ps = m.GetParameters(); } catch { continue; }
                if (ps.Length != 2) continue;
                if (ps[0].ParameterType != typeof(int)) continue;

                // 回调参数的形态两种都要认：ref Action<Sprite>（byref）和值传递的
                // Action<Sprite>（值传递）。实测游戏用的是后者，只认 byref 会永远匹配不上。
                var t = ps[1].ParameterType;
                if (t.IsByRef) t = t.GetElementType();
                if (t == null || !typeof(Delegate).IsAssignableFrom(t)) continue;

                LoadTexture = m;
                return;
            }
        }

        /// <summary>大厅互动场景：LobbySettingItemTable + 道具字典。缺哪条只记下来，不影响主功能。</summary>
        private void ResolveInteraction()
        {
            var missing = new List<string>();

            LobbyType = FindType("Proto.Design.common.LobbySettingItemTable", "LobbySettingItemTable");
            if (LobbyType != null)
            {
                LobbyId = LobbyType.GetProperty("Id");
                LobbySkinId = LobbyType.GetProperty("PrestigeSkinId");
                LobbyKind = LobbyType.GetProperty("Type");
                LobbyInteractionId = LobbyType.GetProperty("InteractionId");
                LobbyCostumeId = LobbyType.GetProperty("CostumeId");
                GetAllLobbyItems = FindNoArgListGetter(LobbyType);

                if (LobbyId == null || LobbySkinId == null) missing.Add("LobbySettingItemTable.Id/PrestigeSkinId");
                if (GetAllLobbyItems == null) missing.Add("GetAllLobbySettingItems");
            }
            else
            {
                missing.Add("LobbySettingItemTable");
            }

            ItemType = FindType("Proto.Net.ItemDBInfo", "ItemDBInfo");
            if (ItemType != null)
            {
                ItemId = ItemType.GetProperty("Id");
                ItemIsNew = ItemType.GetProperty("IsNew");
                ItemKind = ItemType.GetProperty("Type");
                ItemInvenIndex = ItemType.GetProperty("InvenIndex");
                ItemCount = ItemType.GetProperty("Count");
                ItemTimeValue = ItemType.GetProperty("TimeValue");
                ItemKeepFlag = ItemType.GetProperty("KeepFlag");
                ItemDict = FindItemDict();

                if (ItemId == null) missing.Add("ItemDBInfo.Id");
                if (ItemDict == null) missing.Add("道具字典");
            }
            else
            {
                missing.Add("ItemDBInfo");
            }

            InteractionProblem = missing.Count == 0 ? "" : string.Join("、", missing);
            ResolveExtraTargets();
        }

        /// <summary>
        /// 「心契之约」相关补丁的可选目标：互动解锁判定、审查开关、互动界面布尔门、
        /// 约会服装谓词、大厅立绘。全部按名字/签名宽松匹配，找不到只记原因。
        /// </summary>
        private void ResolveExtraTargets()
        {
            var missing = new List<string>();

            InteractionUnlockCheck = FindBoolMethod("CheckInteractionUnlock", 2);
            if (InteractionUnlockCheck == null) missing.Add("CheckInteractionUnlock");

            CensorshipType = FindType("Proto.Net.CensorshipManager", "CensorshipManager");
            if (CensorshipType != null)
                CensorshipCheck = FindBoolMethodOn(CensorshipType, "IsCensorship");
            if (CensorshipCheck == null) missing.Add("CensorshipManager.IsCensorship");

            UserDbType = FindType("Proto.Net.UserDBInfo", "UserDBInfo");
            if (UserDbType != null)
            {
                PortraitCostumeId = UserDbType.GetProperty("UsePortraitCostumeId");
                PortraitDesignId = UserDbType.GetProperty("UsePortraitCostumeDesignTableID");
            }
            if (PortraitCostumeId == null || PortraitDesignId == null)
                missing.Add("UserDBInfo 大厅立绘属性");

            SpineType = FindType("SpineInteractionUI", "SpineInteractionUI");
            if (SpineType != null)
            {
                foreach (var m in SpineType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (m.Name != "SetUI") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 2) continue;
                        if (!ps[1].ParameterType.IsGenericType ||
                            ps[1].ParameterType.GetGenericTypeDefinition() != typeof(List<>)) continue;
                        if (ps[1].ParameterType.GetGenericArguments()[0] != typeof(int)) continue;
                        SpineSetUI = m;
                        break;
                    }
                    catch { }
                }

                foreach (var p in SpineType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (p.PropertyType != typeof(bool) || p.GetIndexParameters().Length != 0) continue;
                        var n = p.Name;
                        if (n.IndexOf("Can", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Possible", StringComparison.OrdinalIgnoreCase) >= 0)
                            SpineGateGetters.Add(p);
                    }
                    catch { }
                }

                // 互动界面刷新方法通常是 void Xxx(int, int)；给它挂一个恢复工具栏的 postfix。
                foreach (var m in SpineType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (m.ReturnType != typeof(void)) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(int) &&
                            ps[1].ParameterType == typeof(int))
                            SpineRestoreTargets.Add(m);
                    }
                    catch { }
                }
            }
            // SetUI 与约会谓词属于实验开关，缺失不算基础功能故障；
            // 需要时由 ExtraUnlocks 在开关打开的情况下补一条 Complaint。

            DatingFriendsType = FindType("DatingFriends", "DatingFriends");
            if (DatingFriendsType != null)
            {
                var nested = new List<Type>();
                try { nested.AddRange(DatingFriendsType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)); }
                catch { }
                if (nested.Count == 0) nested.Add(DatingFriendsType);

                foreach (var t in nested)
                {
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (m.ReturnType != typeof(bool)) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1) continue;
                            var pn = ps[0].ParameterType.Name;
                            if (pn.IndexOf("DatingCostumeTable", StringComparison.Ordinal) < 0) continue;
                            DatingPredicates.Add(m);
                        }
                        catch { }
                    }
                }
            }
            ExtraProblem = missing.Count == 0 ? "" : string.Join("、", missing);
        }

        private static MethodInfo FindBoolMethod(string name, int maxParameters)
        {
            foreach (var asm in SafeAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (StartsWithAny(asmName, SystemPrefixes)) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;
                    var m = FindBoolMethodOn(t, name, maxParameters);
                    if (m != null) return m;
                }
            }
            return null;
        }

        private static MethodInfo FindBoolMethodOn(Type type, string name, int maxParameters = 2)
        {
            foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    if (m.ReturnType != typeof(bool)) continue;
                    if (m.Name != name && m.Name != "get_" + name) continue;
                    var ps = m.GetParameters();
                    if (ps.Length > maxParameters) continue;
                    var ok = true;
                    foreach (var p in ps)
                    {
                        if (p.ParameterType.IsByRef || p.IsOut) { ok = false; break; }
                    }
                    if (ok) return m;
                }
                catch { }
            }
            return null;
        }

        /// <summary>静态、无参、返回 <c>List&lt;T&gt;</c> 的方法（GetAllLobbySettingItems 就是这形态）。</summary>
        private static MethodInfo FindNoArgListGetter(Type element)
        {
            foreach (var asm in SafeAssemblies())
            {
                var name = asm.GetName().Name ?? "";
                if (StartsWithAny(name, SystemPrefixes)) continue;

                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        try
                        {
                            if (m.GetParameters().Length != 0) continue;

                            var rt = m.ReturnType;
                            if (!rt.IsGenericType || rt.GetGenericTypeDefinition() != typeof(List<>)) continue;
                            if (rt.GetGenericArguments()[0] != element) continue;

                            return m;
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 道具字典。宿主类上要同时有 <c>List&lt;ItemDBInfo&gt;</c> 的静态属性（说明这是道具管理类），
        /// 再取 <c>Dictionary&lt;int, ItemDBInfo&gt;</c> 静态字段；有 Type==25 条目的优先。
        /// </summary>
        private FieldInfo FindItemDict()
        {
            if (ItemType == null) return null;

            var listType = typeof(List<>).MakeGenericType(ItemType);

            foreach (var asm in SafeAssemblies())
            {
                var name = asm.GetName().Name ?? "";
                if (StartsWithAny(name, SystemPrefixes)) continue;

                foreach (var t in SafeTypes(asm))
                {
                    if (t == null) continue;

                    try
                    {
                        var hasListProperty = false;
                        foreach (var p in t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (p.PropertyType == listType) { hasListProperty = true; break; }
                        }
                        if (!hasListProperty) continue;

                        var candidates = new List<FieldInfo>();
                        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (!typeof(IDictionary).IsAssignableFrom(f.FieldType)) continue;
                            if (ValueTypeOf(f.FieldType) != ItemType) continue;
                            candidates.Add(f);
                        }
                        if (candidates.Count == 0) continue;

                        foreach (var f in candidates)
                        {
                            var dict = f.GetValue(null) as IDictionary;
                            if (dict == null || dict.Count == 0) continue;

                            foreach (DictionaryEntry e in dict)
                            {
                                try
                                {
                                    if (ItemKind != null &&
                                        Convert.ToInt32(ItemKind.GetValue(e.Value, null)) == 25)
                                        return f;
                                }
                                catch { }
                            }
                        }

                        return candidates[0];
                    }
                    catch { }
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- 工具

        internal static IEnumerable<Assembly> SafeAssemblies()
        {
            Assembly[] all;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return Array.Empty<Assembly>(); }
            return all;
        }

        internal static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { return Array.Empty<Type>(); }

            return types ?? Array.Empty<Type>();
        }

        private static bool StartsWithAny(string s, string[] prefixes)
        {
            foreach (var p in prefixes)
            {
                if (s.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>当前皮肤字典实例。游戏重新加载数据时会换成新对象，用它判断是否需要重建注入。</summary>
        internal IDictionary GetSkinDict() => SafeGet(SkinDict);
        internal IDictionary GetCostumeDict() => SafeGet(CostumeDict);

        /// <summary>救赎之塔每局重建的服装字典列表（可能为空）。</summary>
        internal List<IDictionary> GetTowerCostumeDicts()
        {
            var result = new List<IDictionary>();
            foreach (var f in TowerCostumeDicts)
            {
                var dict = SafeGet(f);
                if (dict != null) result.Add(dict);
            }
            return result;
        }

        /// <summary>全局服装字典 + 塔内服装字典，供注入/回放时统一遍历。</summary>
        internal List<IDictionary> GetAllCostumeDicts()
        {
            var result = new List<IDictionary>();
            var main = SafeGet(CostumeDict);
            if (main != null) result.Add(main);
            result.AddRange(GetTowerCostumeDicts());
            return result;
        }

        /// <summary>塔内当前镜像的服装件数；没进塔时为 0，用于排查。</summary>
        internal int TowerCostumeCount()
        {
            var total = 0;
            foreach (var dict in GetTowerCostumeDicts())
            {
                try { total += dict.Count; }
                catch { }
            }
            return total;
        }
        internal IDictionary GetItemDict() => SafeGet(ItemDict);

        /// <summary>
        /// 读取任意 DB_COMMON 表的完整行。RawDataManager.GetTableOfIds 只给 id，
        /// 还要用 GetValueObject&lt;T&gt;(enum, string, int) 逐行取对象。
        /// 表未就绪、方法签名不匹配时返回 null，由调用方决定是否回退。
        /// </summary>
        internal IList GetTableRows(string tableName, Type rowType)
        {
            if (GetTableIds == null || DbCommon == null || rowType == null) return null;

            var rdm = GetRawDataManagerInstance();
            if (rdm == null) return null;

            IList ids;
            try
            {
                ids = GetTableIds.Invoke(rdm, new object[] { DbCommon, tableName }) as IList;
            }
            catch { return null; }
            if (ids == null || ids.Count == 0) return null;

            var getter = ResolveValueObject(rowType);
            if (getter == null) return null;

            var rows = new List<object>(ids.Count);
            foreach (var id in ids)
            {
                try
                {
                    var row = getter.Invoke(rdm, new object[] { DbCommon, tableName, Convert.ToInt32(id) });
                    if (row != null) rows.Add(row);
                }
                catch { }
            }
            return rows;
        }

        private object GetRawDataManagerInstance()
        {
            if (_rdmInstance != null) return _rdmInstance;
            if (GetTableIds == null) return null;

            var rdmType = GetTableIds.DeclaringType;
            if (rdmType == null) return null;

            var self = rdmType;
            while (self != null)
            {
                try
                {
                    foreach (var p in self.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length != 0) continue;
                            if (!rdmType.IsAssignableFrom(p.PropertyType)) continue;
                            var v = p.GetValue(null, null);
                            if (v != null) return _rdmInstance = v;
                        }
                        catch { }
                    }

                    foreach (var f in self.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (!rdmType.IsAssignableFrom(f.FieldType)) continue;
                            var v = f.GetValue(null);
                            if (v != null) return _rdmInstance = v;
                        }
                        catch { }
                    }
                }
                catch { }

                self = self.BaseType;
            }
            return null;
        }

        private MethodInfo ResolveValueObject(Type rowType)
        {
            if (_getValueObject != null && _getValueRowType == rowType) return _getValueObject;

            var rdmType = GetTableIds?.DeclaringType;
            if (rdmType == null) return null;

            MethodInfo candidate = null;
            try
            {
                foreach (var m in rdmType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (m.Name != "GetValueObject") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 3) continue;
                        if (!ps[0].ParameterType.IsEnum || ps[1].ParameterType != typeof(string)) continue;
                        if (ps[2].ParameterType != typeof(int)) continue;
                        candidate = m;
                        break;
                    }
                    catch { }
                }

                if (candidate != null && candidate.IsGenericMethodDefinition)
                    candidate = candidate.MakeGenericMethod(rowType);
            }
            catch { candidate = null; }

            _getValueObject = candidate;
            _getValueRowType = rowType;
            return candidate;
        }

        /// <summary>
        /// 账号数据是否已经下发。登出后服装字典会被游戏清空，
        /// 此时任何数据库读取都可能触发「db is not loaded」，插件必须停手等下一次登录。
        /// </summary>
        internal bool HasAccountData()
        {
            var costumes = GetCostumeDict();
            return costumes != null && costumes.Count > 0;
        }

        /// <summary>查询设计表行；查询失败时保留「不可用」，不能把它当成缺行。</summary>
        internal TableRowStatus LookupTableRow(int designId, out object row)
        {
            row = null;
            if (designId == 0) return TableRowStatus.Missing;
            if (GetTableRow == null) return TableRowStatus.Unavailable;

            try
            {
                row = GetTableRow.Invoke(null, new object[] { designId });
                return row == null ? TableRowStatus.Missing : TableRowStatus.Found;
            }
            catch
            {
                return TableRowStatus.Unavailable;
            }
        }

        private static IDictionary SafeGet(FieldInfo f)
        {
            if (f == null) return null;
            try { return f.GetValue(null) as IDictionary; }
            catch { return null; }
        }

        internal string Describe()
        {
            var costumeHost = CostumeDict?.DeclaringType?.Name ?? "?";
            var skinHost = SkinDict?.DeclaringType?.Name ?? "?";
            var itemHost = ItemDict?.DeclaringType?.Name ?? "?";
            var tower = TowerCostumeDicts.Count == 0 ? ("失败：" + TowerProblem) : (TowerCostumeDicts.Count + " 个");
            var extra = ExtraProblem.Length == 0
                ? $"互动门={SpineGateGetters.Count} 约会谓词={DatingPredicates.Count} 互动界面={SpineRestoreTargets.Count}"
                : ("部分缺失：" + ExtraProblem);
            return $"服装字典 {costumeHost}.{CostumeDict?.Name} / 皮肤字典 {skinHost}.{SkinDict?.Name} / " +
                   $"道具字典 {itemHost}.{ItemDict?.Name} / 塔字典 {tower} / {extra}";
        }
    }
}
