using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 大厅互动场景的解锁。
    ///
    /// 皮肤不止出现在服装弹窗里：<c>LobbySettingItemTable</c> 里 <c>PrestigeSkinId != 0</c> 的那些
    /// 大厅道具，本质是「某个互动场景 = 某套皮肤」。这些道具要存在于 <c>ItemDBInfo</c> 字典里
    /// 才会出现在大厅，未购入时自然也没有。
    ///
    /// 打开「解锁全部互动」后，<c>InteractionId &gt; 0</c> 的其它互动条目也会一并补进来；
    /// 同时把 <c>Type == 2</c> 的互动 row.Id 收集给心契之约界面使用。
    /// </summary>
    internal static class InteractionScenes
    {
        private static readonly HashSet<int> Injected = new HashSet<int>();
        private static readonly List<int> CovenantIds = new List<int>();
        private static object _bound;
        private static bool _includeAll;

        internal static int InjectedCount => Injected.Count;

        /// <summary>心契之约界面可用的互动 row.Id（Type=2 且 InteractionId&gt;0）。</summary>
        internal static IReadOnlyList<int> AllInteractionIds => CovenantIds;

        /// <summary>补齐大厅互动道具。返回本次新增条数。</summary>
        internal static int Inject(GameRefs r, bool includeAll = false)
        {
            if (_includeAll != includeAll)
            {
                Rollback(r);
                _includeAll = includeAll;
            }

            if (r == null || !r.Ready) return 0;

            // 登出过程中 GetAllLobbySettingItems 会读已卸载的数据库，
            // 游戏会在插件 catch 到异常之前就把「db is not loaded」报出来。
            if (!r.HasAccountData()) return 0;

            if (r.ItemDict == null || r.ItemType == null || r.ItemId == null) return 0;
            if (r.GetAllLobbyItems == null || r.LobbySkinId == null || r.LobbyId == null) return 0;

            var dict = r.GetItemDict();
            if (dict == null) return 0;

            // 重登/换号会整体换掉字典，之前的记账随之作废
            if (!ReferenceEquals(dict, _bound)) { _bound = dict; Injected.Clear(); }

            IList rows;
            try { rows = r.GetAllLobbyItems.Invoke(null, null) as IList; }
            catch { return 0; }
            if (rows == null) return 0;

            var added = 0;
            var covenant = new List<int>();

            foreach (var row in rows)
            {
                if (row == null) continue;

                int itemId, skinId, interactionId, kind;
                try
                {
                    skinId = ReadInt(r.LobbySkinId, row);
                    interactionId = ReadInt(r.LobbyInteractionId, row);
                    kind = ReadInt(r.LobbyKind, row);
                    itemId = ReadInt(r.LobbyId, row);
                }
                catch { continue; }

                if (itemId == 0) continue;

                // Type=2 的互动条目是心契之约列表用的（row.Id，不是 InteractionId 字段）
                if (kind == 2 && interactionId > 0 && !covenant.Contains(itemId))
                    covenant.Add(itemId);

                var isSkinScene = skinId != 0;
                var isAnyInteraction = includeAll && interactionId > 0;
                if (!isSkinScene && !isAnyInteraction) continue;

                try
                {
                    if (dict.Contains(itemId)) continue;

                    var entry = Activator.CreateInstance(r.ItemType);
                    if (entry == null) continue;

                    r.ItemId.SetValue(entry, itemId, null);
                    try { r.ItemIsNew?.SetValue(entry, false, null); } catch { }
                    try { r.ItemKind?.SetValue(entry, 25, null); } catch { }

                    // 与游戏自身的互动道具保持一致：负数 InvenIndex、数量 1、无计时/保留标记。
                    SetValue(entry, r.ItemInvenIndex, -(100000L + itemId));
                    SetValue(entry, r.ItemCount, 1);
                    SetValue(entry, r.ItemTimeValue, 0L);
                    SetValue(entry, r.ItemKeepFlag, 0);

                    dict[itemId] = entry;
                    Injected.Add(itemId);
                    added++;
                }
                catch
                {
                    // 单条失败不影响其它场景
                }
            }

            CovenantIds.Clear();
            CovenantIds.AddRange(covenant);
            return added;
        }

        private static int ReadInt(PropertyInfo p, object row)
        {
            if (p == null) return 0;
            return Convert.ToInt32(p.GetValue(row, null));
        }

        private static void SetValue(object entry, PropertyInfo p, object value)
        {
            if (p == null) return;
            try
            {
                var converted = value != null && !p.PropertyType.IsInstanceOfType(value)
                    ? Convert.ChangeType(value, p.PropertyType)
                    : value;
                p.SetValue(entry, converted, null);
            }
            catch { }
        }

        /// <summary>退出时把自己补的道具移除。</summary>
        internal static void Rollback(GameRefs r)
        {
            if (Injected.Count == 0) return;

            var dict = r?.GetItemDict();
            if (dict != null)
            {
                foreach (var itemId in Injected)
                {
                    try { if (dict.Contains(itemId)) dict.Remove(itemId); }
                    catch { }
                }
            }

            Injected.Clear();
        }
    }
}
