using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 两个补丁。签名一律走 object / object[] —— 这样插件编译期不需要引用游戏程序集，
    /// 游戏改内部结构时也只是补丁挂不上，而不是整个程序集加载失败。
    /// </summary>
    internal static class Patches
    {
        internal static bool Active;
        internal static GameRefs Refs;
        internal static SkinCatalog Catalog;
        internal static SkinState State;

        internal static Action SaveSelection;
        internal static Action<string> LogInfo;
        internal static Action<string> LogWarn;

        /// <summary>
        /// 已挂上的补丁，退出时逐个撤销。
        /// 不用 UnpatchSelf / UnpatchAll：这两个在不同 Harmony 发行版里签名不一致，
        /// 而 Unpatch(MethodBase, MethodInfo) 是两边都稳定的 API。
        /// </summary>
        private static readonly List<KeyValuePair<MethodBase, MethodInfo>> Applied =
            new List<KeyValuePair<MethodBase, MethodInfo>>();

        // 委托可能还没接上（例如离线测试），统一走空安全调用
        private static void Note(string message) => LogInfo?.Invoke(message);
        private static void Complaint(string message) => LogWarn?.Invoke(message);

        // ------------------------------------------------------------ 安装

        /// <summary>返回真正挂上的补丁数量，调用方可用它判断环境是否正常。</summary>
        internal static int Install(Harmony harmony, GameRefs r)
        {
            var installed = 0;

            if (r.GetConnectedSkins != null)
            {
                if (TryPatch(harmony, r.GetConnectedSkins, nameof(GetConnected_Postfix), false, "查询服装皮肤"))
                    installed++;
            }
            else
            {
                Complaint("没找到 GetConnectedPrestigeSkinDBInfos，皮肤列表不会被补全");
            }

            if (r.SetSkin != null)
            {
                if (TryPatch(harmony, r.SetSkin, nameof(SetSkin_Prefix), true, "切换皮肤"))
                    installed++;
            }
            else
            {
                Complaint("没找到 SetSkin，点击皮肤仍会走服务器（可能失败）");
            }

            if (r.AddSkin != null)
            {
                if (TryPatch(harmony, r.AddSkin, nameof(AddSkin_Prefix), true, "拦截默认皮肤写入"))
                    installed++;
            }
            else
            {
                Complaint("没找到 AddPrestigeSkinDBInfo，默认皮肤条目不会被拦下");
            }

            if (r.LoadTexture != null)
            {
                if (TryPatch(harmony, r.LoadTexture, nameof(LoadTexture_Prefix), true, "贴图回调兜底"))
                    installed++;
            }
            else
            {
                Complaint("没找到 SkinExchangePopupUI.LoadTexture，未拥有皮肤的格子可能一直转圈");
            }

            Note($"已挂载 {installed} 个补丁。");
            return installed;
        }

        private static bool TryPatch(Harmony harmony, MethodInfo target, string patchName, bool isPrefix, string label)
        {
            try
            {
                var method = typeof(Patches).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
                var attr = new HarmonyMethod(method);

                if (!CallPatch(harmony, target, attr, isPrefix, out var error))
                {
                    Complaint($"补丁失败（{label}）：{error}");
                    return false;
                }

                Applied.Add(new KeyValuePair<MethodBase, MethodInfo>(target, method));

                Note($"补丁已挂上：{label} → {target.DeclaringType?.Name}.{target.Name}");
                return true;
            }
            catch (Exception e)
            {
                Complaint($"补丁失败（{label}）：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过反射调用 Harmony.Patch，而不是直接写 harmony.Patch(...)。
        ///
        /// 原因：不同 Harmony 发行版的 Patch 重载参数个数不一样（HarmonyX 多一个
        /// ilmanipulator）。直接调用会把重载绑死在编译期那一版上，用户装了别的
        /// Harmony 版本时就会 MissingMethod —— 而反射调用按运行时实际的签名构造参数，
        /// 两版都能用。这里只调一次，开销可以忽略。
        /// </summary>
        private static bool CallPatch(Harmony harmony, MethodBase target, HarmonyMethod attr, bool isPrefix, out string error)
        {
            error = null;

            try
            {
                foreach (var candidate in typeof(Harmony).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (candidate.Name != "Patch") continue;

                    var ps = candidate.GetParameters();
                    if (ps.Length < 3) continue;
                    if (ps[0].ParameterType != typeof(MethodBase)) continue;
                    if (ps[1].ParameterType != typeof(HarmonyMethod)) continue;
                    if (ps[2].ParameterType != typeof(HarmonyMethod)) continue;

                    var args = new object[ps.Length];
                    args[0] = target;
                    for (var i = 1; i < ps.Length; i++)
                        args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;

                    args[isPrefix ? 1 : 2] = attr;

                    candidate.Invoke(harmony, args);
                    return true;
                }

                error = "Harmony 上没有可用的 Patch 重载";
                return false;
            }
            catch (Exception e)
            {
                error = (e is TargetInvocationException tie && tie.InnerException != null)
                    ? tie.InnerException.Message
                    : e.Message;
                return false;
            }
        }

        internal static void Uninstall(Harmony harmony)
        {
            if (harmony == null) return;

            foreach (var pair in Applied)
            {
                try { harmony.Unpatch(pair.Key, pair.Value); }
                catch (Exception e) { Complaint("撤销补丁失败：" + e.Message); }
            }
            Applied.Clear();
        }

        // ------------------------------------------------------------ 补全皮肤列表

        /// <summary>
        /// 游戏问「这件服装有哪些皮肤」时：
        ///   1. 先剔掉默认皮肤（designId=0）与设计表里查不到的脏条目；
        ///   2. 再把设计表里这套服装的全部皮肤补进去。
        /// 未购买的皮肤本来不在玩家数据里，这一步让它们出现在弹窗中。
        /// </summary>
        private static void GetConnected_Postfix(object __instance, ref object __result)
        {
            if (!Active || __instance == null) return;

            var r = Refs;
            var cat = Catalog;
            if (r == null || !r.Ready || cat == null || !cat.Ready) return;

            try
            {
                var current = __result as IList;
                if (current == null) return;   // 原方法返回 null 时保持原样，不改变语义

                var before = current.Count;

                var cleaned = (IList)Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(r.SkinType));

                foreach (var item in current)
                {
                    if (item == null) continue;

                    int designId;
                    try { designId = Convert.ToInt32(r.SkinDesignId.GetValue(item, null)); }
                    catch { cleaned.Add(item); continue; }

                    // 默认皮肤（0）和表里没有的条目一律不返回
                    if (designId == 0 || !r.InTable(designId)) continue;

                    cleaned.Add(item);
                }

                var added = State?.AppendFor(r, __instance, cleaned, cat) ?? 0;

                // 过滤和补全都没改动，就别换对象，省得界面白白刷新
                if (cleaned.Count == before && added == 0) return;

                __result = cleaned;
            }
            catch (Exception e)
            {
                Complaint("补全皮肤列表失败：" + e.Message);
            }
        }

        // ------------------------------------------------------------ 贴图回调兜底

        /// <summary>
        /// 弹窗给每套皮肤调 LoadTexture(designId, callback) 取头像。
        /// 未购买的皮肤没有本地资源，回调可能永远不来，格子就一直转圈。
        /// 这里把回调包一层：0.5 秒内没回就补 null 收尾。
        ///
        /// 用 object[] __args 而不是 (int, ref Action&lt;Sprite&gt;)，是为了编译期不依赖 UnityEngine；
        /// 写回数组即写回 ref 参数。
        /// </summary>
        private static void LoadTexture_Prefix(object[] __args)
        {
            if (!Active || __args == null || __args.Length < 2) return;

            try
            {
                var designId = Convert.ToInt32(__args[0]);
                var wrapped = TextureGuard.Wrap(__args[1], designId);
                if (wrapped != null) __args[1] = wrapped;
            }
            catch (Exception e)
            {
                Complaint("贴图回调兜底失败：" + e.Message);
            }
        }

        // ------------------------------------------------------------ 拦截默认皮肤写入

        /// <summary>
        /// 游戏往皮肤字典写条目时，designId 为 0（默认皮肤）或设计表里查不到的，直接不写。
        /// 与参考实现一致——这类条目会让弹窗里多出空皮肤。
        /// </summary>
        private static bool AddSkin_Prefix(object __0)
        {
            if (!Active || __0 == null) return true;

            var r = Refs;
            if (r == null || !r.Ready) return true;

            try
            {
                var designId = Convert.ToInt32(r.SkinDesignId.GetValue(__0, null));
                if (designId == 0) return false;
                if (r.GetTableRow != null && !r.InTable(designId)) return false;
            }
            catch (Exception e)
            {
                Complaint("拦截皮肤写入时出错，交还原方法：" + e.Message);
            }

            return true;
        }

        // ------------------------------------------------------------ 本地切换

        /// <summary>
        /// 玩家点了某套皮肤：只改本地内存并记下选择，然后跳过原方法
        /// —— 原方法会向服务器发切换请求，而服务器并不认为玩家拥有这套皮肤。
        /// </summary>
        private static bool SetSkin_Prefix(object[] __args)
        {
            if (!Active) return true;

            try
            {
                var designId = Convert.ToInt32(__args[0]);
                var isSet = __args[1] is bool b && b;
                var callback = __args[2] as Action;

                if (State == null || !State.ApplyLocal(Refs, designId, isSet, out var costumeId))
                {
                    // 找不到条目说明这套皮肤没进过我们的目录，交还给原方法更安全
                    return true;
                }

                Note($"本地切换：costumeId={costumeId} designId={designId} isSet={isSet}");
                SaveSelection?.Invoke();

                try { callback?.Invoke(); }
                catch (Exception e) { Complaint("切换回调抛出异常：" + e.Message); }

                return false;
            }
            catch (Exception e)
            {
                Complaint("SetSkin 补丁出错，交还原方法：" + e.Message);
                return true;
            }
        }
    }
}
