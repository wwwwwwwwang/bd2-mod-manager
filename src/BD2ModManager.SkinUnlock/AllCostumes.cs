using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 本地服装镜像。把所有「带皮肤的服装」补进玩家服装字典，
    /// 让未拥有的服装也能在本地列表里出现。只影响客户端显示，
    /// 服务器仍然认为这些服装未拥有；默认关闭。
    /// </summary>
    internal static class AllCostumes
    {
        private static readonly HashSet<int> Injected = new HashSet<int>();
        private static object _bound;

        internal static int InjectedCount => Injected.Count;

        internal static int Apply(GameRefs r, SkinCatalog catalog, bool enabled)
        {
            if (!enabled)
            {
                Rollback(r);
                return 0;
            }

            if (r == null || !r.Ready || catalog == null || !catalog.Ready) return 0;
            if (!r.HasAccountData()) return 0;

            IDictionary dict;
            try { dict = r.CostumeDict?.GetValue(null) as IDictionary; }
            catch { return 0; }
            if (dict == null) return 0;

            if (!ReferenceEquals(dict, _bound))
            {
                _bound = dict;
                Injected.Clear();
            }

            var keyType = DictKeyType(dict);
            var level = r.CostumeType.GetProperty("Level");
            var inven = r.CostumeType.GetProperty("InvenIndex");
            var sort = r.CostumeType.GetProperty("SortId");

            var added = 0;
            foreach (var costumeId in catalog.CostumeIds)
            {
                try
                {
                    var key = Convert.ChangeType(costumeId, keyType);
                    if (dict.Contains(key)) continue;
                    if (Injected.Contains(costumeId)) continue;

                    var entry = Activator.CreateInstance(r.CostumeType);
                    if (entry == null) continue;

                    Set(r.CostumeId, entry, key);
                    if (catalog.TryGetFirstDesign(costumeId, out var designId))
                        Set(r.CostumeDesignId, entry, designId);

                    if (level != null) Set(level, entry, 1);
                    if (inven != null) Set(inven, entry, 800000000L + costumeId);
                    if (sort != null) Set(sort, entry, 0);

                    dict[key] = entry;
                    Injected.Add(costumeId);
                    added++;
                }
                catch
                {
                    // 单件失败不影响其它服装
                }
            }

            return added;
        }

        internal static void Rollback(GameRefs r)
        {
            if (Injected.Count == 0) return;

            IDictionary dict = null;
            try { dict = r?.CostumeDict?.GetValue(null) as IDictionary; }
            catch { }

            if (dict != null)
            {
                var keyType = DictKeyType(dict);
                foreach (var costumeId in Injected)
                {
                    try { dict.Remove(Convert.ChangeType(costumeId, keyType)); }
                    catch { }
                }
            }

            Injected.Clear();
        }

        private static Type DictKeyType(IDictionary dict)
        {
            var t = dict.GetType();
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Length == 2) return args[0];
            }
            return typeof(int);
        }

        private static void Set(PropertyInfo p, object target, object value)
        {
            if (p == null) return;
            try
            {
                var converted = value != null && !p.PropertyType.IsInstanceOfType(value)
                    ? Convert.ChangeType(value, p.PropertyType)
                    : value;
                p.SetValue(target, converted, null);
            }
            catch { }
        }
    }
}
