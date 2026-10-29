using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 皮肤弹窗的贴图回调兜底。
    ///
    /// 问题：弹窗给每套皮肤调 <c>LoadTexture(designId, callback)</c> 去异步取头像。
    /// 未购买的皮肤没有下载过资源，回调可能一直不来，格子上就永久转圈。
    ///
    /// 做法：把回调包一层，0.5 秒内没被调用就自己补一次 <c>null</c> 让 UI 收尾；
    /// 若回调正常到达，则原样转发一次，之后不再触发。
    ///
    /// 刻意不引用 UnityEngine：回调是 <c>Action&lt;Sprite&gt;</c>，这里用「参数类型运行时取得 +
    /// 泛型闭包委托」的方式包，编译期不需要 Sprite 类型，离线测试也能跑。
    /// </summary>
    internal static class TextureGuard
    {
        /// <summary>多久没回调就补 null，与参考实现一致。</summary>
        internal const double TimeoutSeconds = 0.5;

        private sealed class Pending
        {
            internal Delegate Original;
            internal bool Invoked;
            internal double StartedAt;
            internal int DesignId;

            internal void Fire(object value)
            {
                if (Invoked) return;
                Invoked = true;

                try { Original?.DynamicInvoke(new[] { value }); }
                catch { /* 界面回调自己抛异常与我们无关，不能让它冒泡进游戏主循环 */ }
            }
        }

        private static readonly List<Pending> Items = new List<Pending>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>超时补 null 的次数，仅用于日志。</summary>
        internal static int TimeoutCount { get; private set; }

        internal static int PendingCount => Items.Count;

        internal static double Now => Clock.Elapsed.TotalSeconds;

        /// <summary>
        /// 包一层。返回原回调或包装后的委托。
        /// 不是「单参数引用类型委托」的一律原样返回，不去猜。
        /// </summary>
        internal static object Wrap(object callback, int designId)
        {
            if (callback == null) return null;

            var type = callback.GetType();
            if (!typeof(Delegate).IsAssignableFrom(type)) return callback;

            MethodInfo invoke;
            ParameterInfo[] ps;
            try
            {
                invoke = type.GetMethod("Invoke");
                if (invoke == null) return callback;
                ps = invoke.GetParameters();
            }
            catch { return callback; }

            if (ps.Length != 1) return callback;

            var argType = ps[0].ParameterType;
            if (argType.IsValueType) return callback;   // 值类型没法安全地传 null

            var pending = new Pending
            {
                Original = (Delegate)callback,
                StartedAt = Now,
                DesignId = designId,
            };

            try
            {
                // Dispatch<T>(object holder, T value)：把 T 定为运行时的参数类型，
                // 再把第一个参数绑到 pending 上，得到一个签名正好是 void(T) 的委托。
                var dispatch = typeof(TextureGuard)
                    .GetMethod(nameof(Dispatch), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(argType);

                var wrapped = Delegate.CreateDelegate(type, pending, dispatch, false);
                if (wrapped == null) return callback;

                Items.Add(pending);
                return wrapped;
            }
            catch
            {
                return callback;
            }
        }

        /// <summary>包装后的委托最终落到这里：只触发一次。</summary>
        private static void Dispatch<T>(object holder, T value)
        {
            var pending = holder as Pending;
            pending?.Fire(value);
        }

        /// <summary>
        /// 每帧调用：超时没回调的补一次 null。
        /// 传入当前秒数便于离线验证，不强依赖真实时钟。
        /// </summary>
        internal static int Tick(double now)
        {
            if (Items.Count == 0) return 0;

            var fired = 0;

            for (var i = Items.Count - 1; i >= 0; i--)
            {
                var p = Items[i];

                if (!p.Invoked)
                {
                    if (now - p.StartedAt <= TimeoutSeconds) continue;

                    // 超时：补一次 null，让界面结束等待
                    p.Fire(null);
                    TimeoutCount++;
                    fired++;
                }

                Items.RemoveAt(i);
            }

            return fired;
        }

        /// <summary>退出时清空，避免持有界面的委托不放。</summary>
        internal static void Clear()
        {
            Items.Clear();
        }
    }
}
