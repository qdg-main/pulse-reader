using System.Runtime.InteropServices;

namespace PulseReader.Speech;

internal static class ContextualPronunciationMemoryPolicy
{
    internal const ulong EstimatedClassifierBytes = 2560UL * 1024 * 1024;
    internal const ulong MinimumAvailableBytes = 3072UL * 1024 * 1024;
    internal const uint CriticalMemoryLoadPercent = 90;

    public static bool CanStart(ContextualPronunciationMemorySnapshot? snapshot)
    {
        if (snapshot is not { } status)
        {
            return true;
        }

        return status.MemoryLoadPercent < CriticalMemoryLoadPercent
            && status.AvailablePhysicalBytes
                >= RetainedHeadroom(status.TotalPhysicalBytes) + EstimatedClassifierBytes;
    }

    public static bool ShouldRelease(ContextualPronunciationMemorySnapshot? snapshot)
    {
        if (snapshot is not { } status)
        {
            return false;
        }

        return status.MemoryLoadPercent >= CriticalMemoryLoadPercent
            || status.AvailablePhysicalBytes < RetainedHeadroom(status.TotalPhysicalBytes);
    }

    private static ulong RetainedHeadroom(ulong totalPhysicalBytes) =>
        Math.Max(MinimumAvailableBytes, totalPhysicalBytes / 10);
}

internal sealed class WindowsContextualPronunciationMemoryMonitor
{
    public ContextualPronunciationMemorySnapshot? Capture()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(ref status)
            ? new ContextualPronunciationMemorySnapshot(
                status.TotalPhysicalBytes,
                status.AvailablePhysicalBytes,
                status.MemoryLoadPercent)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }

        public uint Length;
        public uint MemoryLoadPercent;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }
}

internal readonly record struct ContextualPronunciationMemorySnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint MemoryLoadPercent);
