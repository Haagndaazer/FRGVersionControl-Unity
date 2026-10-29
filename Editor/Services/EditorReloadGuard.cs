using System;
using System.Threading;
using UnityEditor;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Holds <see cref="EditorApplication.LockReloadAssemblies"/> for the lifetime of a long
    /// working-copy operation (commit, update) so a script recompile cannot tear the IPC client
    /// down mid-operation. Counted; the release runs on the main thread from whatever thread the
    /// awaiting continuation lands on, and is idempotent per hold.
    /// </summary>
    public static class EditorReloadGuard
    {
        private static int s_Depth;

        public static IDisposable Hold()
        {
            if (SvnCommandRunner.IsMainThread)
            {
                Acquire();
            }
            else
            {
                SvnCommandRunner.s_UnityMainSync.Send(_ => Acquire(), null);
            }
            return new Release();
        }

        public static bool IsHeld => Volatile.Read(ref s_Depth) > 0;

        private static void Acquire()
        {
            if (Interlocked.Increment(ref s_Depth) == 1)
            {
                try { EditorApplication.LockReloadAssemblies(); } catch { }
            }
        }

        private static void ReleaseOne()
        {
            if (Volatile.Read(ref s_Depth) <= 0) return;
            if (Interlocked.Decrement(ref s_Depth) == 0)
            {
                try { EditorApplication.UnlockReloadAssemblies(); } catch { }
            }
        }

        private sealed class Release : IDisposable
        {
            private int m_Released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref m_Released, 1) == 1) return;
                if (SvnCommandRunner.IsMainThread) ReleaseOne();
                else SvnCommandRunner.s_UnityMainSync.Post(_ => ReleaseOne(), null);
            }
        }
    }
}
