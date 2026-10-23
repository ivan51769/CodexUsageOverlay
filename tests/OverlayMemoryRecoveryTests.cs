using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CodexUsageOverlay;

internal static class OverlayMemoryRecoveryTests
{
    private const long MiB = 1024 * 1024;

    internal static void Verify()
    {
        DateTime now = DateTime.UtcNow;
        Expect(false, 120, 30, 0, true, true, now, now);
        Expect(false, 400, 300, 0, true, true, now, now);
        Expect(false, 400, 30, 0, false, true, now, now);
        Expect(false, 400, 30, 0, true, false, now, now);
        Expect(false, 400, 30, 0, true, true, now, now.AddMinutes(5));
        Expect(true, 400, 30, 0, true, true, now, now);
        Expect(false, 400, 30, 380, true, true, now, now);
        Expect(true, 444, 30, 380, true, true, now, now);

        List<WeakReference> providers = ReleasedProviders();
        int beforeCollections = GC.CollectionCount(GC.MaxGeneration);
        long before;
        using (Process process = Process.GetCurrentProcess()) before = process.PrivateMemorySize64;
        OverlayMemoryRecovery.RecoverUnreferencedProviders();
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 10000 &&
            (GC.CollectionCount(GC.MaxGeneration) == beforeCollections || HasLiveProvider(providers)))
            Thread.Sleep(20);
        if (GC.CollectionCount(GC.MaxGeneration) == beforeCollections || HasLiveProvider(providers))
            throw new Exception("Released COM providers survived the background collection");
        long after;
        using (Process process = Process.GetCurrentProcess()) after = process.PrivateMemorySize64;
        Console.WriteLine("Released COM providers=20000; commit before=" + before + "; after=" + after +
            "; background collection milliseconds=" + wait.ElapsedMilliseconds);
    }

    private static void Expect(bool expected, long privateMiB, long managedMiB, long previousMiB,
        bool sustained, bool idle, DateTime now, DateTime next)
    {
        if (OverlayMemoryRecovery.ShouldRecover(privateMiB * MiB, managedMiB * MiB,
            previousMiB * MiB, sustained, idle, now, next) != expected)
            throw new Exception("Unexpected native-memory recovery at " + privateMiB + " MiB");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> ReleasedProviders()
    {
        var references = new List<WeakReference>();
        for (int i = 0; i < 20000; i++)
        {
            var provider = new ComProvider();
            IntPtr pointer = Marshal.GetIUnknownForObject(provider);
            Marshal.Release(pointer);
            references.Add(new WeakReference(provider));
        }
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasLiveProvider(List<WeakReference> providers)
    {
        foreach (WeakReference provider in providers) if (provider.IsAlive) return true;
        return false;
    }

    [ComVisible(true)]
    public sealed class ComProvider { }
}
