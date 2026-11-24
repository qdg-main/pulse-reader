using System.Text.Json;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal sealed class UpdateCheckStateStore(string path)
{
    internal UpdateCheckState Load()
    {
        try
        {
            SetupPaths.AssertSafe(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 8192) return new();
            var state = JsonSerializer.Deserialize<UpdateCheckState>(stream) ?? new();
            if (state.AvailableVersion is not null
                && !UpdateVersionPolicy.TryParseStable(state.AvailableVersion, false, out _))
                state = state with { AvailableVersion = null };
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            return new();
        }
    }

    internal bool TrySave(UpdateCheckState state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            SetupPaths.AssertSafe(path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            SetupPaths.AssertSafe(path);
            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            return false;
        }
        finally
        {
            if (ownsTemporary)
            {
                try { SetupPaths.AssertSafe(temporary); File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.WriteLine(exception); }
            }
        }
    }

    internal static bool IsDue(UpdateCheckState state, DateTimeOffset now)
    {
        if (IsDeferred(state, now)) return false;
        return state.LastAttempt is not { } attempted
            || attempted > now || now - attempted >= UpdateConstants.CheckInterval;
    }

    internal static bool IsDeferred(UpdateCheckState state, DateTimeOffset now) =>
        state.RetryAfter is { } retry && retry > now
        && retry <= now + UpdateConstants.CheckInterval;
}
