using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseReader.Updates;

internal static class UpdateOperationLog
{
    internal static void Append(string path, string message)
    {
        // Inspect the opened file itself before writing; never follow a link or
        // append diagnostics to a user file via a forged log hard link.
        using var handle = CreateFile(path, 0x40000000, 1, IntPtr.Zero, 4, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)
            || (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || info.Links != 1) throw new InvalidDataException("The operation log is not a regular single-link file.");
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
