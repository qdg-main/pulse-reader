using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PulseReader.Setup;

internal interface IDistributionTermsReceiptStore
{
    string? ReadAcceptedSha256();
    void WriteAcceptedSha256(string sha256);
}

internal sealed class RegistryDistributionTermsReceiptStore : IDistributionTermsReceiptStore
{
    internal const string RegistryPath = @"Software\QuantumDotGames\PulseReader";
    internal const string ValueName = "AcceptedDistributionTermsSha256";

    public string? ReadAcceptedSha256()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void WriteAcceptedSha256(string sha256)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new IOException("The distribution terms receipt could not be opened.");
        key.SetValue(ValueName, sha256, RegistryValueKind.String);
        key.Flush();
        if (!string.Equals(key.GetValue(ValueName) as string, sha256, StringComparison.Ordinal))
            throw new IOException("The distribution terms receipt could not be verified.");
    }
}

internal sealed record DistributionTermsDocument(string Text, string Sha256);

internal enum DistributionTermsConsentStatus
{
    Allowed,
    Declined,
    ManualAcceptanceRequired,
    Error
}

internal sealed record DistributionTermsConsentResult(DistributionTermsConsentStatus Status, string? Error = null)
{
    internal bool CanStart => Status == DistributionTermsConsentStatus.Allowed;
}

internal static class DistributionTermsConsent
{
    internal const string ContractFileName = "distribution-terms.json";
    internal const string TermsFileName = "Distribution-Terms.txt";
    internal const int MaximumContractBytes = 16 * 1024;
    internal const int MaximumTermsBytes = 256 * 1024;

    // This gate runs before the application context can create hotkeys or start model setup.
    internal static DistributionTermsConsentResult Check(
        string applicationRoot,
        bool isStartupLaunch,
        IDistributionTermsReceiptStore receipts,
        Func<DistributionTermsDocument, bool> askForAcceptance)
    {
        try
        {
            var contractPath = SetupPaths.Child(applicationRoot, ContractFileName);
            if (!Exists(contractPath))
            {
                if (!Exists(SetupPaths.Child(applicationRoot, "native-runtime-manifest.json")))
                    return new(DistributionTermsConsentStatus.Allowed);
                throw new InvalidDataException("The packaged copy is missing distribution-terms.json.");
            }

            var terms = ReadTerms(applicationRoot, contractPath);
            if (string.Equals(receipts.ReadAcceptedSha256(), terms.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(DistributionTermsConsentStatus.Allowed);

            if (isStartupLaunch)
                return new(DistributionTermsConsentStatus.ManualAcceptanceRequired,
                    "Open Pulse Reader manually to review and accept its distribution terms before it can run at sign-in.");

            if (!askForAcceptance(terms))
                return new(DistributionTermsConsentStatus.Declined);

            receipts.WriteAcceptedSha256(terms.Sha256);
            return new(DistributionTermsConsentStatus.Allowed);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or SecurityException or JsonException or ArgumentException or NotSupportedException)
        {
            return new(DistributionTermsConsentStatus.Error,
                "Pulse Reader could not verify or record acceptance of its distribution terms. "
                + "The application has not started. Restore the complete package from the official release "
                + "and check that your Windows account can save its acceptance receipt.\n\n" + exception.Message);
        }
    }

    private static DistributionTermsDocument ReadTerms(string root, string contractPath)
    {
        using var contract = JsonDocument.Parse(ReadBounded(contractPath, MaximumContractBytes),
            new JsonDocumentOptions { MaxDepth = 4 });
        var properties = contract.RootElement.ValueKind == JsonValueKind.Object
            ? contract.RootElement.EnumerateObject().ToArray()
            : throw new InvalidDataException("The distribution terms contract must be a JSON object.");
        string[] names = ["SchemaVersion", "TermsFileName", "TermsSha256"];
        if (properties.Length != names.Length
            || names.Any(name => properties.Count(property => property.Name == name) != 1))
            throw new InvalidDataException("The distribution terms contract has missing or unexpected fields.");
        var version = contract.RootElement.GetProperty("SchemaVersion");
        var name = contract.RootElement.GetProperty("TermsFileName");
        var checksum = contract.RootElement.GetProperty("TermsSha256");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1
            || name.ValueKind != JsonValueKind.String || name.GetString() != TermsFileName
            || checksum.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The distribution terms contract has an unsupported schema or unsafe terms filename.");
        var expectedSha256 = checksum.GetString();
        if (expectedSha256 is null || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The distribution terms checksum must contain exactly 64 hexadecimal characters.");

        var bytes = ReadBounded(SetupPaths.Child(root, TermsFileName), MaximumTermsBytes);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Distribution-Terms.txt does not match the packaged distribution terms checksum.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            throw new InvalidDataException("The distribution terms text is empty or contains invalid text characters.");
        return new(text, actualSha256);
    }

    private static bool Exists(string path)
    {
        // File.Exists hides access errors, which must not make a package look like a development build.
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        SetupPaths.AssertSafe(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} must contain between 1 and {maximumBytes} bytes.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"{Path.GetFileName(path)} changed while it was being read.");
        return bytes;
    }
}
