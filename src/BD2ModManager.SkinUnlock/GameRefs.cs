using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
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
        internal MethodInfo GetAllLobbyItems;     // static () → List<LobbySettingItemTable>

        internal Type ItemType;                   // Proto.Net.ItemDBInfo
        internal PropertyInfo ItemId;
        internal PropertyInfo ItemIsNew;
        internal PropertyInfo ItemKind;           // Type，注入时固定写 25
        internal FieldInfo ItemDict;              // Dictionary<int, ItemDBInfo>

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
                ItemDict = FindItemDict();

                if (ItemId == null) missing.Add("ItemDBInfo.Id");
                if (ItemDict == null) missing.Add("道具字典");
            }
            else
            {
                missing.Add("ItemDBInfo");
            }

            InteractionProblem = missing.Count == 0 ? "" : string.Join("、", missing);
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
        internal IDictionary GetItemDict() => SafeGet(ItemDict);

        /// <summary>该 designId 在皮肤设计表里是否有对应行。</summary>
        internal bool InTable(int designId)
        {
            if (GetTableRow == null || designId == 0) return false;
            try { return GetTableRow.Invoke(null, new object[] { designId }) != null; }
            catch { return false; }
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
            return $"服装字典 {costumeHost}.{CostumeDict?.Name} / 皮肤字典 {skinHost}.{SkinDict?.Name} / 道具字典 {itemHost}.{ItemDict?.Name}";
        }
    }
}
