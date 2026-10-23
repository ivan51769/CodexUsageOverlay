using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexUsageOverlay
{
    // UIA's short-lived COM providers can retain substantial native memory while
    // the small managed heap never reaches the normal generation-2 GC budget.
    internal sealed class OverlayMemoryRecovery : IDisposable
    {
        private const long MiB = 1024 * 1024;
        private readonly Timer timer;
        private int running;
        private volatile bool disposed;
        private bool pressureObserved, awaitingResult;
        private long lastRecoveryBytes;
        private DateTime nextRecoveryUtc;

        internal OverlayMemoryRecovery()
        {
            timer = new Timer(Check, null, 30000, 30000);
        }

        internal static bool ShouldRecover(long privateBytes, long managedBytes, long previousBytes,
            bool sustained, bool inputIdle, DateTime now, DateTime nextAllowed)
        {
            return sustained && inputIdle && now >= nextAllowed && privateBytes >= 256 * MiB &&
                privateBytes - managedBytes >= 192 * MiB &&
                (previousBytes == 0 || privateBytes - previousBytes >= 64 * MiB);
        }

        private void Check(object ignored)
        {
            if (disposed || Interlocked.Exchange(ref running, 1) != 0) return;
            try
            {
                long privateBytes;
                using (Process process = Process.GetCurrentProcess()) privateBytes = process.PrivateMemorySize64;
                long managedBytes = GC.GetTotalMemory(false);
                if (awaitingResult)
                {
                    WriteDiagnostic("observed", lastRecoveryBytes, privateBytes, managedBytes);
                    lastRecoveryBytes = privateBytes;
                    awaitingResult = false;
                }
                // Finalization/native heaps may settle after the first result sample.
                if (lastRecoveryBytes > 0 && privateBytes < lastRecoveryBytes)
                    lastRecoveryBytes = privateBytes;
                bool sustained = pressureObserved;
                pressureObserved = privateBytes >= 256 * MiB && privateBytes - managedBytes >= 192 * MiB;
                DateTime now = DateTime.UtcNow;
                if (!ShouldRecover(privateBytes, managedBytes, lastRecoveryBytes, sustained,
                    InputIdle(), now, nextRecoveryUtc) || disposed) return;
                nextRecoveryUtc = now.AddMinutes(5);
                lastRecoveryBytes = privateBytes;
                awaitingResult = true;
                WriteDiagnostic("requested", privateBytes, privateBytes, managedBytes);
                RecoverUnreferencedProviders();
            }
            catch { } // Resource diagnostics must not interrupt the overlay.
            finally { Interlocked.Exchange(ref running, 0); }
        }

        internal static void RecoverUnreferencedProviders()
        {
            // Ask for a background full collection; never trim the working set,
            // wait on finalizers, or discard live caches/other process resources.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
        }

        private static bool InputIdle()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            return GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount - info.Time) >= 3000;
        }

        private static void WriteDiagnostic(string phase, long before, long current, long managed)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "memory-recovery.log"),
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " phase=" + phase +
                    "; beforeBytes=" + before + "; privateBytes=" + current + "; managedBytes=" + managed +
                    "; gen2Collections=" + GC.CollectionCount(GC.MaxGeneration));
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo { internal uint Size, Time; }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        public void Dispose() { disposed = true; timer.Dispose(); }
    }
}
