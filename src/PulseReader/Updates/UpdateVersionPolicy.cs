using System.Globalization;
using System.Text.RegularExpressions;

namespace PulseReader.Updates;

internal static partial class UpdateVersionPolicy
{
    internal static bool TryParseStable(string? text, bool tag, out Version? version)
    {
        version = null;
        if (text is null) return false;
        var match = StableVersion().Match(text);
        if (!match.Success || (tag ? match.Groups[1].Value != "v" : match.Groups[1].Value != "")) return false;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        version = new Version(major, minor, patch);
        return true;
    }

    internal static Version Normalize(Version version)
    {
        if (version.Build < 0 || version.Revision > 0)
            throw new InvalidDataException("The running application must have a three-part release version.");
        return new Version(version.Major, version.Minor, version.Build);
    }

    internal static bool IsVariant(string? value) => value is "win-x64" or "win-x64-framework-dependent";

    internal static string AssetName(Version version, string variant, UpdatePackageKind kind)
    {
        if (!IsVariant(variant) || !Enum.IsDefined(kind))
            throw new InvalidDataException("The update package identity is unsupported.");
        return $"PulseReader-{Normalize(version)}-{variant}-{(kind == UpdatePackageKind.Installer ? "Setup.exe" : "Portable.zip")}";
    }

    [GeneratedRegex(@"\A(v?)(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersion();
}
