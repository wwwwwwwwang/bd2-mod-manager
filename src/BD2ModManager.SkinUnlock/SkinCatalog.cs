using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 从游戏自带的设计表里读出「每件服装 → 它有哪些皮肤」。
    ///
    /// 不硬编码任何 id：游戏更新后新增的皮肤会自动出现。
    /// </summary>
    internal sealed class SkinCatalog
    {
        private readonly Dictionary<int, List<int>> _byCostume = new Dictionary<int, List<int>>();

        internal bool Ready { get; private set; }
        internal int SkinCount { get; private set; }
        internal int CostumeCount => _byCostume.Count;

        private int _attempts;
        private object _rawDataManager;

        internal bool TryGetSkins(int costumeId, out List<int> designIds)
            => _byCostume.TryGetValue(costumeId, out designIds);

        /// <summary>
        /// 构建目录。数据表没加载好时返回 false，调用方过一会再试。
        /// </summary>
        internal bool TryBuild(GameRefs r, Action<string> log, Action<string> warn)
        {
            if (Ready) return true;
            if (r == null || !r.Ready) return false;

            // 服装字典还没被游戏填满，说明数据尚未下发
            var costumes = r.GetCostumeDict();
            if (costumes == null || costumes.Count == 0) return false;

            try
            {
                var rdm = GetRawDataManager(r);
                if (rdm == null) return WarnOnce(ref _attempts, "拿不到 RawDataManager 实例", warn);

                if (!(r.GetTableIds.Invoke(rdm, new object[] { r.DbCommon, "PrestigeSkinTable" }) is IList rows)
                    || rows.Count == 0)
                {
                    return WarnOnce(ref _attempts, "PrestigeSkinTable 还没就绪", warn);
                }

                _byCostume.Clear();
                var skins = 0;

                foreach (var row in rows)
                {
                    try
                    {
                        int designId, costumeId;

                        // 大多数实现返回的是 designId 列表，个别版本直接返回表行
                        if (row is int id)
                        {
                            designId = id;
                            var line = r.GetTableRow.Invoke(null, new object[] { designId });
                            if (line == null) continue;
                            costumeId = Convert.ToInt32(r.TableCostumeId.GetValue(line, null));
                        }
                        else
                        {
                            if (!r.TableType.IsInstanceOfType(row)) continue;
                            designId = Convert.ToInt32(r.TableId.GetValue(row, null));
                            costumeId = Convert.ToInt32(r.TableCostumeId.GetValue(row, null));
                        }

                        if (designId == 0 || costumeId == 0) continue;

                        if (!_byCostume.TryGetValue(costumeId, out var list))
                        {
                            list = new List<int>(4);
                            _byCostume[costumeId] = list;
                        }
                        if (!list.Contains(designId))
                        {
                            list.Add(designId);
                            skins++;
                        }
                    }
                    catch (Exception e)
                    {
                        warn("读一条皮肤表记录失败：" + e.Message);
                    }
                }

                if (skins == 0)
                {
                    return WarnOnce(ref _attempts, "PrestigeSkinTable 读到了 0 条皮肤", warn);
                }

                SkinCount = skins;
                Ready = true;

                log($"皮肤目录就绪：{_byCostume.Count} 件服装、{skins} 套皮肤");
                return true;
            }
            catch (Exception e)
            {
                return WarnOnce(ref _attempts, "读皮肤表出错：" + e.Message, warn);
            }
        }

        private static bool WarnOnce(ref int attempts, string why, Action<string> warn)
        {
            attempts++;
            if (attempts <= 3) warn(why + "（稍后重试）");
            return false;
        }

        /// <summary>
        /// RawDataManager 是单例，但实例可能挂在静态属性或静态字段上 —— 两种都找。
        /// </summary>
        private object GetRawDataManager(GameRefs r)
        {
            if (_rawDataManager != null) return _rawDataManager;

            var rdmType = r.GetTableIds.DeclaringType;
            if (rdmType == null) return null;

            var self = rdmType;
            while (self != null)
            {
                foreach (var p in self.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (p.GetIndexParameters().Length != 0) continue;
                        if (!rdmType.IsAssignableFrom(p.PropertyType)) continue;
                        var v = p.GetValue(null, null);
                        if (v != null) return _rawDataManager = v;
                    }
                    catch { }
                }

                foreach (var f in self.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (!rdmType.IsAssignableFrom(f.FieldType)) continue;
                        var v = f.GetValue(null);
                        if (v != null) return _rawDataManager = v;
                    }
                    catch { }
                }

                self = self.BaseType;
            }

            return null;
        }

        internal void Reset()
        {
            _byCostume.Clear();
            Ready = false;
            SkinCount = 0;
            _attempts = 0;
            _rawDataManager = null;
        }
    }
}
