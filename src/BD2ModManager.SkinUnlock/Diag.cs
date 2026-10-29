using System;
using System.IO;
using System.Text;
using BepInEx;

namespace Suiren.BD2.SkinUnlock
{
    /// <summary>
    /// 落地探针：绕开 BepInEx 日志系统，直接写文件。
    ///
    /// 为什么需要它：BepInEx 的磁盘日志监听器一旦写文件失败（被杀软或别的进程短暂独占），
    /// 会把自己从监听器列表里摘掉，之后插件再怎么打日志都不会落盘。
    /// 这时「插件没日志」既可能是插件没跑，也可能是日志通道死了 —— 必须分开判断。
    ///
    /// 只在启动阶段与状态变化时写，有行数上限，不参与任何业务逻辑。
    /// </summary>
    internal static class Diag
    {
        private const int MaxLines = 800;

        private static readonly object Gate = new object();

        private static StreamWriter _writer;
        private static int _lines;
        private static bool _dead;

        internal static string FilePath { get; private set; }

        internal static void Open(string note)
        {
            try
            {
                FilePath = Path.Combine(Paths.BepInExRootPath, "SkinUnlock.probe.log");

                // FileShare.ReadWrite：探针要能在游戏运行时被外部读取
                var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                Note(note);
            }
            catch
            {
                _dead = true;
            }
        }

        internal static void Note(string message)
        {
            if (_dead) return;

            lock (Gate)
            {
                if (_writer == null || _lines >= MaxLines) return;
                _lines++;

                try
                {
                    _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
                }
                catch
                {
                    _dead = true;
                }
            }
        }

        internal static void Close()
        {
            lock (Gate)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
