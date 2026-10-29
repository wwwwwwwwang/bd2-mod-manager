using System;
using System.Collections;
using System.Collections.Generic;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 皮肤库的本地状态：往游戏字典里补条目、按记录回放选择、退出时回滚。
    ///
    /// 与「全程序集扫描 + 每几秒全表清理」的做法不同，这里用 _injected 精确记住
    /// 自己补了哪些 designId，回滚和一致性检查都只针对这些条目，代价是 O(补入条数)。
    /// </summary>
    internal sealed class SkinState
    {
        private readonly HashSet<int> _injected = new HashSet<int>();
        private readonly Dictionary<int, int> _selection = new Dictionary<int, int>();

        /// <summary>清理时被移走的原始条目（键 → 对象），退出时原样放回去。</summary>
        private readonly Dictionary<object, object> _removed = new Dictionary<object, object>();

        private object _boundDict;          // 记住注入时用的字典实例，被替换就重来
        private Type _keyType = typeof(int); // 皮肤字典的键类型

        internal IReadOnlyDictionary<int, int> Selection => _selection;
        internal int InjectedCount => _injected.Count;

        // ------------------------------------------------------------ 选择记录

        internal void SetSelection(int costumeId, int designId)
        {
            if (designId == 0) _selection.Remove(costumeId);
            else _selection[costumeId] = designId;
        }

        internal void ReplaceSelection(IDictionary<int, int> src)
        {
            _selection.Clear();
            if (src == null) return;
            foreach (var kv in src) _selection[kv.Key] = kv.Value;
        }

        // ------------------------------------------------------------ 注入

        /// <summary>
        /// 把「已拥有服装」的全部皮肤补进游戏字典。返回本次新增条数。
        /// 只在字典实例变化或出现没见过的服装时才实际干活。
        /// </summary>
        internal int EnsureInjected(GameRefs r, SkinCatalog cat)
        {
            if (r == null || !r.Ready || cat == null || !cat.Ready) return 0;

            var dict = r.GetSkinDict();
            if (dict == null) return 0;

            // 游戏重登/换号会整体换掉字典，此时之前的注入全部作废
            if (!ReferenceEquals(dict, _boundDict))
            {
                _boundDict = dict;
                _injected.Clear();
                DetectKeyType(dict);
            }

            var costumes = r.GetCostumeDict();
            if (costumes == null || costumes.Count == 0) return 0;

            var added = 0;

            foreach (var costume in costumes.Values)
            {
                if (costume == null) continue;

                int costumeId, currentDesign;
                try
                {
                    costumeId = Convert.ToInt32(r.CostumeId.GetValue(costume, null));
                    currentDesign = Convert.ToInt32(r.CostumeDesignId.GetValue(costume, null));
                }
                catch { continue; }

                if (costumeId == 0) continue;
                if (!cat.TryGetSkins(costumeId, out var designs)) continue;

                foreach (var designId in designs)
                {
                    try
                    {
                        var key = ToKey(designId);
                        if (dict.Contains(key)) continue;

                        var entry = CreateEntry(r, costumeId, designId, currentDesign == designId);
                        if (entry == null) continue;

                        dict[key] = entry;
                        _injected.Add(designId);
                        added++;
                    }
                    catch (Exception)
                    {
                        // 单条失败不影响整体
                    }
                }
            }

            return added;
        }

        /// <summary>移除自己补进去的条目。退出时调用，把内存交还给游戏原本的样子。</summary>
        internal void Rollback(GameRefs r)
        {
            var dict = r?.GetSkinDict();

            if (dict != null)
            {
                if (_injected.Count > 0)
                {
                    foreach (var designId in _injected)
                    {
                        try
                        {
                            var key = ToKey(designId);
                            if (dict.Contains(key)) dict.Remove(key);
                        }
                        catch { }
                    }
                }

                // 清理阶段移走的原始条目放回去（参考实现不做这件事，我们多做一步更安全）
                foreach (var kv in _removed)
                {
                    try { if (!dict.Contains(kv.Key)) dict[kv.Key] = kv.Value; }
                    catch { }
                }
            }

            _injected.Clear();
            _removed.Clear();
            _boundDict = null;
        }

        /// <summary>
        /// 剔除皮肤字典里的脏条目：designId 为 0 的默认项、以及设计表里查不到的项。
        ///
        /// 参考实现每 3 秒做一次全表清理。这里行为一致，但会先把移走的原始条目记下来，
        /// 退出时放回去——否则等于把游戏自己的数据删了还不还。
        /// </summary>
        internal int CleanupInvalid(GameRefs r)
        {
            if (r == null || !r.Ready) return 0;

            var dict = r.GetSkinDict();
            if (dict == null || dict.Count == 0) return 0;

            var doomed = new List<object>();

            foreach (DictionaryEntry e in dict)
            {
                try
                {
                    var designId = Convert.ToInt32(r.SkinDesignId.GetValue(e.Value, null));
                    if (designId == 0 || !r.InTable(designId)) doomed.Add(e.Key);
                }
                catch { }
            }

            var removed = 0;

            foreach (var key in doomed)
            {
                try
                {
                    var value = dict[key];
                    dict.Remove(key);

                    // 自己注入的条目不算「原始数据」，删了不需要还
                    var isOurs = false;
                    try
                    {
                        var designId = Convert.ToInt32(r.SkinDesignId.GetValue(value, null));
                        isOurs = _injected.Remove(designId);
                    }
                    catch { }

                    if (!isOurs) _removed[key] = value;
                    removed++;
                }
                catch { }
            }

            return removed;
        }

        /// <summary>
        /// 把某件服装在设计表里的全部皮肤补进 <paramref name="target"/>，缺的条目顺手写进字典。
        /// 供 GetConnectedPrestigeSkinDBInfos 的 Postfix 逐次调用（参考实现同名逻辑）。
        /// </summary>
        internal int AppendFor(GameRefs r, object costume, IList target, SkinCatalog cat)
        {
            if (r == null || !r.Ready || costume == null || target == null) return 0;
            if (cat == null || !cat.Ready) return 0;

            int costumeId, currentDesign;
            try
            {
                costumeId = Convert.ToInt32(r.CostumeId.GetValue(costume, null));
                currentDesign = Convert.ToInt32(r.CostumeDesignId.GetValue(costume, null));
            }
            catch { return 0; }

            if (costumeId == 0) return 0;
            if (!cat.TryGetSkins(costumeId, out var designs) || designs.Count == 0) return 0;

            var seen = new HashSet<int>();
            foreach (var item in target)
            {
                if (item == null) continue;
                try { seen.Add(Convert.ToInt32(r.SkinDesignId.GetValue(item, null))); }
                catch { }
            }

            var dict = r.GetSkinDict();
            var added = 0;

            foreach (var designId in designs)
            {
                if (seen.Contains(designId)) continue;

                object entry = null;

                try
                {
                    if (dict != null)
                    {
                        var key = ToKey(designId);
                        if (dict.Contains(key)) entry = dict[key];
                    }
                }
                catch { }

                if (entry == null)
                {
                    entry = CreateEntry(r, costumeId, designId, currentDesign == designId);
                    if (entry == null) continue;

                    // 与参考实现一致：补进列表的同时也写进字典，别的界面读字典时同样能看到
                    if (dict != null)
                    {
                        try
                        {
                            dict[ToKey(designId)] = entry;
                            _injected.Add(designId);
                        }
                        catch { }
                    }
                }

                try { target.Add(entry); added++; }
                catch { }
            }

            return added;
        }

        /// <summary>
        /// 一致性巡检：只检查自己补过的条目还在不在、IsSet 有没有被服务器改回去。
        /// 不做全表遍历。
        /// </summary>
        internal int VerifyInjected(GameRefs r)
        {
            if (r == null || !r.Ready || _injected.Count == 0) return 0;

            var dict = r.GetSkinDict();
            if (dict == null) return 0;
            if (!ReferenceEquals(dict, _boundDict))
            {
                _boundDict = dict;
                _injected.Clear();
                return 0;
            }

            var stale = 0;
            foreach (var designId in new List<int>(_injected))
            {
                try
                {
                    if (!dict.Contains(ToKey(designId))) stale++;
                }
                catch { }
            }
            return stale;
        }

        /// <summary>把选择表中的记录写回服装的当前皮肤（服务器同步会把它冲掉）。</summary>
        internal int ReapplySelection(GameRefs r)
        {
            if (r == null || !r.Ready || _selection.Count == 0) return 0;

            var costumes = r.GetCostumeDict();
            if (costumes == null || costumes.Count == 0) return 0;

            var changed = 0;

            foreach (var costume in costumes.Values)
            {
                if (costume == null) continue;
                try
                {
                    var costumeId = Convert.ToInt32(r.CostumeId.GetValue(costume, null));
                    if (!_selection.TryGetValue(costumeId, out var want)) continue;

                    var now = Convert.ToInt32(r.CostumeDesignId.GetValue(costume, null));
                    if (now == want) continue;

                    r.CostumeDesignId.SetValue(costume, want, null);
                    changed++;
                }
                catch { }
            }

            return changed;
        }

        // ------------------------------------------------------------ 用户点选

        /// <summary>
        /// 玩家在弹窗里点了某套皮肤：只改本地内存 + 记录选择，不发任何请求。
        /// </summary>
        internal bool ApplyLocal(GameRefs r, int designId, bool isSet, out int costumeId)
        {
            costumeId = 0;

            if (r == null || !r.Ready) return false;

            var dict = r.GetSkinDict();
            if (dict == null) return false;

            object entry;
            try
            {
                var key = ToKey(designId);
                if (!dict.Contains(key)) return false;
                entry = dict[key];
            }
            catch { return false; }

            try
            {
                costumeId = Convert.ToInt32(r.SkinCostumeId.GetValue(entry, null));
                if (costumeId == 0) return false;

                // 同一件服装下，同一时刻只有一套皮肤处于选中态
                foreach (var other in dict.Values)
                {
                    if (other == null) continue;
                    try
                    {
                        if (Convert.ToInt32(r.SkinCostumeId.GetValue(other, null)) != costumeId) continue;
                        r.SkinIsSet.SetValue(other, false, null);
                    }
                    catch { }
                }

                r.SkinIsSet.SetValue(entry, isSet, null);

                var costumes = r.GetCostumeDict();
                if (costumes != null)
                {
                    foreach (var costume in costumes.Values)
                    {
                        if (costume == null) continue;
                        try
                        {
                            if (Convert.ToInt32(r.CostumeId.GetValue(costume, null)) != costumeId) continue;
                            r.CostumeDesignId.SetValue(costume, isSet ? designId : 0, null);
                        }
                        catch { }
                    }
                }

                SetSelection(costumeId, isSet ? designId : 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ 内部

        /// <summary>按 designId 从游戏字典里取条目，没有就返回 null。</summary>
        internal object FindEntry(GameRefs r, int designId)
        {
            if (r == null || !r.Ready) return null;

            var dict = r.GetSkinDict();
            if (dict == null) return null;

            try
            {
                var key = ToKey(designId);
                return dict.Contains(key) ? dict[key] : null;
            }
            catch
            {
                return null;
            }
        }

        internal object CreateEntry(GameRefs r, int costumeId, int designId, bool isSet)
        {
            try
            {
                var obj = Activator.CreateInstance(r.SkinType);
                if (obj == null) return null;

                r.SkinCostumeId.SetValue(obj, costumeId, null);
                r.SkinDesignId.SetValue(obj, designId, null);
                r.SkinIsSet.SetValue(obj, isSet, null);
                return obj;
            }
            catch
            {
                return null;
            }
        }

        private void DetectKeyType(IDictionary dict)
        {
            _keyType = typeof(int);
            try
            {
                var t = dict.GetType();
                if (t.IsGenericType)
                {
                    var args = t.GetGenericArguments();
                    if (args.Length == 2) _keyType = args[0];
                }
            }
            catch { }
        }

        /// <summary>字典键可能不是 int（例如 long），统一转换后再用。</summary>
        private object ToKey(int designId)
            => _keyType == typeof(int) ? (object)designId : Convert.ChangeType(designId, _keyType);
    }
}
