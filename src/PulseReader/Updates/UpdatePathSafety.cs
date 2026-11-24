using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseReader.Updates;

// Windows handles deny concurrent writes/renames while transaction paths are in use.
internal static class UpdatePathSafety
{
    internal static string Relative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240 || value.StartsWith('/') || value.StartsWith('\\'))
            throw new InvalidDataException("Invalid update payload path.");
        var parts = value.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var device = part.Split('.')[0].TrimEnd(' ');
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || device.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789¹²³".Contains(device[3])))
                throw new InvalidDataException("Invalid update payload path.");
        }
        return string.Join('/', parts);
    }

    internal static string Root(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.IndexOf(':', 2) >= 0) throw new InvalidDataException("Invalid portable application root.");
        var root = Path.GetFullPath(value).TrimEnd('\\', '/');
        if (root.Length <= 3 || !root.Equals(value.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid portable application root.");
        Relative(root[3..]);
        return root;
    }

    internal static string Under(string root, string relative) => Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar));

    internal static void AssertRegular(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("Update path is a link or has the wrong file type.");
    }

    internal static void ReplaceJournal(string source, string destination)
    {
        // Rename only this operation's explicit temporary file, with a write-through commit.
        if (!MoveFileEx(source, destination, 0x1 | 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal sealed class Pins : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        internal void Directory(string path, bool create = false)
        {
            path = Path.GetFullPath(path);
            if (path.Length > 3) path = path.TrimEnd('\\');
            if (_handles.ContainsKey(path)) return;
            var parent = Path.GetDirectoryName(path);
            if (parent is not null) Directory(parent, create);
            if (create && !System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
            AssertRegular(path.Length == 2 ? path + "\\" : path, true);
            var handle = CreateFile(path.Length == 2 ? path + "\\" : path, 0, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (!GetFileInformationByHandle(handle, out var information)
                || (information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != (uint)FileAttributes.Directory)
            { handle.Dispose(); throw new InvalidDataException("Cannot pin an update directory."); }
            _handles.Add(path, handle);
            AssertRegular(path.Length == 2 ? path + "\\" : path, true);
        }
        internal void Parents(string path, bool create = false) => Directory(Path.GetDirectoryName(path)!, create);
        internal void Release(string path)
        {
            if (_handles.Remove(Path.GetFullPath(path).TrimEnd('\\'), out var handle)) handle.Dispose();
        }
        public void Dispose() { foreach (var handle in _handles.Values.Reverse()) handle.Dispose(); _handles.Clear(); }
    }

    internal sealed class LockedFile : IDisposable
    {
        internal FileStream Stream { get; }
        internal string Identity { get; }
        internal LockedFile(string path, bool movable = true)
        {
            AssertRegular(path, false);
            var handle = CreateFile(path, 0x80000000u | (movable ? 0x10000u : 0), 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (!GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            { handle.Dispose(); throw new InvalidDataException("Cannot pin an update file."); }
            Identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
            Stream = new FileStream(handle, FileAccess.Read);
        }
        internal void Move(string destination)
        {
            var name = System.Text.Encoding.Unicode.GetBytes(destination);
            var pointerOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = pointerOffset + IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(lengthOffset + 4 + name.Length + 2);
            try
            {
                for (var i = 0; i < lengthOffset + 4; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, buffer + lengthOffset + 4, name.Length);
                Marshal.WriteInt16(buffer, lengthOffset + 4 + name.Length, 0);
                if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)(lengthOffset + 4 + name.Length + 2)))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        internal void Delete()
        {
            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buffer, 1);
                if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, buffer, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose() => Stream.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr information, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);
}
