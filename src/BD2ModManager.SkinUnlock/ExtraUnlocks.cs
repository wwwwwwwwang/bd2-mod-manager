using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 「心契之约 / 互动 / 审查 / 大厅立绘」相关的可选补丁。
    ///
    /// 这些能力参考了社区付费工具的行为，但实现完全独立：
    ///   - 不引用游戏程序集，补丁目标全部由 <see cref="GameRefs"/> 反射查找；
    ///   - 只改内存里的可见性/判定，不发网络请求、不改存档、不改数值；
    ///   - 每个开关都能单独关闭，关掉时补丁原样放行。
    ///
    /// 默认只开「补全部互动 + 放行互动判定 + 关审查」；心契之约列表和立绘映射
    /// 属于实验性开关，默认关闭，避免不同游戏版本出现界面异常。
    /// </summary>
    internal static class ExtraUnlocks
    {
        internal static bool UnlockAllInteractions = true;
        internal static bool BypassInteractionUnlock = true;
        internal static bool DisableCensorship = true;
        internal static bool UnlockCovenantList = false;
        internal static bool LobbyPortrait = true;
        internal static int LobbyDesignId = 0;

        internal static void Configure(bool unlockAll, bool bypass, bool noCensorship,
            bool covenantList, bool lobbyPortrait, int lobbyDesignId)
        {
            UnlockAllInteractions = unlockAll;
            BypassInteractionUnlock = bypass;
            DisableCensorship = noCensorship;
            UnlockCovenantList = covenantList;
            LobbyPortrait = lobbyPortrait;
            LobbyDesignId = lobbyDesignId;
        }

        internal static int Install(Harmony harmony, GameRefs r)
        {
            if (r == null) return 0;
            var count = 0;

            // 补丁始终挂载，具体开关在补丁方法里判断。这样用户在配置管理器里
            // 改完开关后无需重启游戏，下一次调用就生效。
            if (r.InteractionUnlockCheck != null)
            {
                if (Patches.TryPatch(harmony, r.InteractionUnlockCheck, nameof(UnlockCheck_Postfix),
                        false, "互动解锁判定", typeof(ExtraUnlocks)))
                    count++;
            }
            else if (BypassInteractionUnlock)
            {
                Patches.Complaint("没找到 CheckInteractionUnlock，互动解锁判定不会放行");
            }

            if (r.CensorshipCheck != null)
            {
                if (Patches.TryPatch(harmony, r.CensorshipCheck, nameof(Censorship_Postfix),
                        false, "内容审查", typeof(ExtraUnlocks)))
                    count++;
            }
            else if (DisableCensorship)
            {
                Patches.Complaint("没找到 CensorshipManager.IsCensorship，内容审查保持原样");
            }

            if (r.SpineSetUI != null)
            {
                if (Patches.TryPatch(harmony, r.SpineSetUI, nameof(SpineSetUI_Prefix),
                        true, "心契之约互动列表", typeof(ExtraUnlocks)))
                    count++;
            }
            else if (UnlockCovenantList)
            {
                Patches.Complaint("没找到 SpineInteractionUI.SetUI，心契之约列表不会替换");
            }

            foreach (var p in r.SpineGateGetters)
            {
                var getter = p.GetGetMethod(true);
                if (getter == null) continue;
                if (Patches.TryPatch(harmony, getter, nameof(True_Postfix),
                        false, "互动界面放行（" + p.Name + "）", typeof(ExtraUnlocks)))
                    count++;
            }

            foreach (var m in r.DatingPredicates)
            {
                if (Patches.TryPatch(harmony, m, nameof(True_Postfix),
                        false, "约会服装谓词", typeof(ExtraUnlocks)))
                    count++;
            }

            // 心契之约/互动界面刷新后，工具栏和缩放条可能被原版逻辑关掉，这里补回来。
            foreach (var m in r.SpineRestoreTargets)
            {
                if (Patches.TryPatch(harmony, m, nameof(RestoreInteraction_Postfix),
                        false, "互动界面恢复", typeof(ExtraUnlocks)))
                    count++;
            }

            if (r.PortraitCostumeId != null)
            {
                var getter = r.PortraitCostumeId.GetGetMethod(true);
                if (getter != null &&
                    Patches.TryPatch(harmony, getter, nameof(PortraitCostume_Postfix),
                        false, "大厅立绘槽位", typeof(ExtraUnlocks)))
                    count++;
            }

            if (r.PortraitDesignId != null)
            {
                var getter = r.PortraitDesignId.GetGetMethod(true);
                if (getter != null &&
                    Patches.TryPatch(harmony, getter, nameof(PortraitDesign_Postfix),
                        false, "大厅立绘映射", typeof(ExtraUnlocks)))
                    count++;
            }

            if ((LobbyPortrait || LobbyDesignId > 0) &&
                r.PortraitCostumeId == null && r.PortraitDesignId == null)
                Patches.Complaint("没找到 UserDBInfo 的大厅立绘属性，立绘映射不会生效");

            return count;
        }

        // ------------------------------------------------------------ 补丁方法

        private static void UnlockCheck_Postfix(ref bool __result)
        {
            if (Patches.Active && BypassInteractionUnlock) __result = true;
        }

        private static void Censorship_Postfix(ref bool __result)
        {
            if (Patches.Active && DisableCensorship) __result = false;
        }

        private static void SpineSetUI_Prefix(ref List<int> __1)
        {
            if (!Patches.Active || !UnlockCovenantList) return;
            var ids = InteractionScenes.AllInteractionIds;
            if (ids == null || ids.Count == 0) return;
            __1 = new List<int>(ids);
        }

        private static void True_Postfix(ref bool __result)
        {
            if (Patches.Active && UnlockCovenantList) __result = true;
        }

        /// <summary>
        /// 付费插件的 RestoreInteraction：把互动界面被原版关掉的工具栏、缩放条重新打开，
        /// 并换一句提示。只操作 UI 对象，不发请求、不写存档。
        /// </summary>
        private static void RestoreInteraction_Postfix(object __instance)
        {
            if (!Patches.Active || !UnlockCovenantList || __instance == null) return;

            var type = __instance.GetType();
            try { SetActive(Field(type, __instance, "_interactionToolbarParent")); } catch { }
            try { Invoke(Field(type, __instance, "_spineInteractionGaugeController"), "SetActiveUI", true); } catch { }
            try { Invoke(Field(type, __instance, "_dragZoomController"), "SetActiveZoomGauge", true); } catch { }
            try { SetText(Field(type, __instance, "_textDescription")); } catch { }
        }

        private static object Field(Type type, object instance, string name)
        {
            var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f?.GetValue(instance);
        }

        private static void SetActive(object component)
        {
            if (component == null) return;
            var go = component.GetType().GetProperty("gameObject")?.GetValue(component, null);
            go?.GetType().GetMethod("SetActive", new[] { typeof(bool) })?.Invoke(go, new object[] { true });
        }

        private static void Invoke(object target, string method, bool value)
        {
            if (target == null) return;
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            m?.Invoke(target, new object[] { value });
        }

        private static void SetText(object component)
        {
            if (component == null) return;
            var p = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p?.SetValue(component, "点击画面或拖拽，即可与服装角色互动。", null);
        }

        private static void PortraitCostume_Postfix(ref int __result)
        {
            if (!Patches.Active) return;

            // 只有强制指定 designId 时才动这个槽位；自动模式只映射 designId，
            // 避免把所有服装都强行切到同一个立绘槽。
            if (LobbyDesignId > 0) __result = 1;
        }

        private static void PortraitDesign_Postfix(ref int __result)
        {
            if (!Patches.Active) return;

            if (LobbyDesignId > 0)
            {
                __result = LobbyDesignId;
                return;
            }

            if (!LobbyPortrait || __result == 0) return;

            var r = Patches.Refs;
            var state = Patches.State;
            if (r == null || state == null || state.Selection == null) return;

            try
            {
                if (r.LookupTableRow(__result, out var row) != TableRowStatus.Found || row == null) return;
                if (r.TableCostumeId == null) return;

                var costume = Convert.ToInt32(r.TableCostumeId.GetValue(row, null));
                if (state.Selection.TryGetValue(costume, out var design) && design != 0)
                    __result = design;
            }
            catch { }
        }
    }
}
