namespace PulseReader.Setup;

internal static class SetupPaths
{
    internal static void AssertSafe(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Model setup refuses a reparse point: {cursor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    internal static string Child(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (string.IsNullOrEmpty(relative) || relative.StartsWith('/') || relative.Contains(':')
            || relative.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException("An installation path is unsafe.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An installation path leaves its root.");
        AssertSafe(path);
        return path;
    }

    internal static void DeleteTree(string path)
    {
        AssertSafe(path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            AssertSafe(entry);
            if (Directory.Exists(entry)) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }
}
