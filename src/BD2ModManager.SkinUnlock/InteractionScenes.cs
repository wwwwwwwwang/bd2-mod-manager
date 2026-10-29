using System;
using System.Collections;
using System.Collections.Generic;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 大厅互动场景的解锁。
    ///
    /// 皮肤不止出现在服装弹窗里：<c>LobbySettingItemTable</c> 里 <c>PrestigeSkinId != 0</c> 的那些
    /// 大厅道具，本质是「某个互动场景 = 某套皮肤」。这些道具要存在于 <c>ItemDBInfo</c> 字典里
    /// 才会出现在大厅，未购入时自然也没有。
    ///
    /// 这里按设计表把这些道具补进字典（Type 固定写 25，与游戏自身的道具类型一致），并记账以便退出时还原。
    /// </summary>
    internal static class InteractionScenes
    {
        private static readonly HashSet<int> Injected = new HashSet<int>();
        private static object _bound;

        internal static int InjectedCount => Injected.Count;

        /// <summary>补齐大厅互动道具。返回本次新增条数。</summary>
        internal static int Inject(GameRefs r)
        {
            if (r == null || !r.Ready) return 0;
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

            foreach (var row in rows)
            {
                if (row == null) continue;

                int itemId, skinId;
                try
                {
                    skinId = Convert.ToInt32(r.LobbySkinId.GetValue(row, null));
                    if (skinId == 0) continue;                      // 这个大厅设定不是皮肤场景
                    itemId = Convert.ToInt32(r.LobbyId.GetValue(row, null));
                }
                catch { continue; }

                if (itemId == 0) continue;

                try
                {
                    if (dict.Contains(itemId)) continue;

                    var entry = Activator.CreateInstance(r.ItemType);
                    if (entry == null) continue;

                    r.ItemId.SetValue(entry, itemId, null);
                    try { r.ItemIsNew?.SetValue(entry, false, null); } catch { }
                    try { r.ItemKind?.SetValue(entry, 25, null); } catch { }

                    dict[itemId] = entry;
                    Injected.Add(itemId);
                    added++;
                }
                catch
                {
                    // 单条失败不影响其它场景
                }
            }

            return added;
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
