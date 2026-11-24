using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Updates;

internal static class PortableUpdateChecks
{
    public static async Task RunAsync()
    {
        await Success();
        await Rejections();
        await RollbackAndCancellation();
        await Recovery();
        Console.WriteLine("PASS: portable updates replace tracked payloads, preserve unknown files, reject unsafe ZIPs, and recover interruptions without erasing later edits.");
    }

    private static async Task Success()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Target, "config.json"), "user settings");
        Directory.CreateDirectory(Path.Combine(fixture.Target, "Models"));
        File.WriteAllText(Path.Combine(fixture.Target, "Models", "personal.onnx"), "personal model");
        File.WriteAllText(Path.Combine(fixture.Target, "notes.txt"), "personal notes");
        await new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None);
        Require(AssemblyName.GetAssemblyName(Path.Combine(fixture.Target, "PulseReader.dll")).Version == new Version("1.1.0.0"), "New application assembly was not published.");
        Require(!File.Exists(Path.Combine(fixture.Target, "obsolete.dll")), "Retired owned payload file was not removed.");
        Require(File.ReadAllText(Path.Combine(fixture.Target, "licenses", "new.txt")) == "new nested license", "Nested payload file was not published.");
        Require(File.ReadAllText(Path.Combine(fixture.Target, "config.json")) == "user settings"
            && File.ReadAllText(Path.Combine(fixture.Target, "Models", "personal.onnx")) == "personal model"
            && File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")) == "personal notes", "Unknown user files were modified.");
        Require(!File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "Successful transaction retained its journal.");
        Require(!Directory.EnumerateDirectories(fixture.Root, ".PulseReader-*").Any(), "Successful transaction retained staging or backup.");
        Require(ReadVersion(fixture.Target) == "1.1.0", "Packaged version was not updated.");

        using var slim = new Fixture("win-x64-framework-dependent");
        await new PortableUpdateTransaction().ApplyAsync(slim.Prepare(), CancellationToken.None);
        Require(ReadVersion(slim.Target) == "1.1.0", "Framework-dependent portable update failed.");
    }

    private static async Task Rejections()
    {
        foreach (var unsafePath in new[] { "../escape.txt", "..\\escape.txt", "/escape.txt", "C:/escape.txt", "file.txt:ads", "dir./file", "dir /file",
            "NUL.txt", "aux/file", "COM¹.txt", "file\u0001.txt", "file\\\\name", "a/../b" })
        {
            using var fixture = new Fixture();
            var prepared = fixture.Prepare(extraEntries: [(unsafePath, "bad", 0)]);
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        foreach (var extra in new[]
        {
            new[] { ("pulsereader.exe", "duplicate", 0) },
            new[] { ("link", "target", unchecked((int)(0xA1FFu << 16))) },
            new[] { ("link", "target", (int)FileAttributes.ReparsePoint) },
            new[] { ("installation-scope.json", "{}", 0) },
            new[] { ("not-in-list.txt", "untracked", 0) },
            new[] { ("folder", "file", 0), ("folder/child", "child", 0) }
        })
        {
            using var fixture = new Fixture();
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(extraEntries: extra), CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            Directory.CreateDirectory(Path.Combine(fixture.Target, "licenses"));
            File.WriteAllText(Path.Combine(fixture.Target, "licenses", "new.txt"), "user collision");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
            fixture.RequireOriginal();
            Require(File.ReadAllText(Path.Combine(fixture.Target, "licenses", "new.txt")) == "user collision", "Unowned collision was erased.");
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare(variant: "win-x64-framework-dependent");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        foreach (var missing in new[] { "PulseReader.exe", "PulseReader.dll", "PulseReader.deps.json", "PulseReader.runtimeconfig.json" })
        {
            using var fixture = new Fixture();
            var prepared = fixture.Prepare(modify: package =>
            {
                package.Remove(missing); RefreshList(package);
            });
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
            Require(!File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target))
                && !Directory.EnumerateDirectories(fixture.Root, ".PulseReader-*").Any(), "Malformed runnable payload changed ownership or retained transaction directories.");
        }
        foreach (var dll in new[] { VersionedApplicationDll("1.0.0"), Encoding.UTF8.GetBytes("not an assembly") })
        {
            using var fixture = new Fixture();
            var prepared = fixture.Prepare(modify: package => package["PulseReader.dll"] = dll);
            var preparedCheckpointReached = false;
            await Reject(() => new PortableUpdateTransaction(point => preparedCheckpointReached |= point == "Prepared").ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
            Require(!preparedCheckpointReached && !File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "Invalid application DLL reached the durable transaction boundary.");
        }
        using (var fixture = new Fixture())
        {
            var mismatch = VersionedApplicationDll("1.1.0");
            for (var offset = 0; offset <= mismatch.Length - 24; offset++)
            {
                if (BitConverter.ToUInt32(mismatch, offset) != 0xFEEF04BD) continue;
                BitConverter.GetBytes(0x00010000u).CopyTo(mismatch, offset + 8);
                BitConverter.GetBytes(0u).CopyTo(mismatch, offset + 12); break;
            }
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(modify: package => package["PulseReader.dll"] = mismatch), CancellationToken.None));
            fixture.RequireOriginal();
        }
        foreach (var name in new[] { "PulseReader.deps.json", "PulseReader.runtimeconfig.json" })
        {
            using var fixture = new Fixture();
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(modify: package => package[name] = Encoding.UTF8.GetBytes("{}")), CancellationToken.None));
            fixture.RequireOriginal();
        }
        foreach (var (name, text) in new[]
        {
            ("PulseReader.runtimeconfig.json", "{\"runtimeOptions\":{}}"),
            ("PulseReader.deps.json", "{\"runtimeTarget\":{\"name\":\"missing\"},\"targets\":{}}")
        })
        {
            using var fixture = new Fixture();
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(modify: package => package[name] = Encoding.UTF8.GetBytes(text)), CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            Directory.CreateDirectory(Path.Combine(fixture.Target, "licenses"));
            File.WriteAllText(Path.Combine(fixture.Target, "licenses", "new.txt"), "late unowned collision");
            Exception? failure = null;
            try { await new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None); }
            catch (InvalidDataException exception) { failure = exception; }
            Require(failure?.Message.Contains("unowned", StringComparison.Ordinal) == true, "Preflight cleanup masked the original collision error.");
            fixture.RequireOriginal();
            Require(!Directory.EnumerateDirectories(fixture.Root, ".PulseReader-*").Any(), "Nested backup parents were left after rejected preflight.");
        }
        using (var fixture = new Fixture())
        {
            File.Delete(Path.Combine(fixture.Target, "obsolete.dll"));
            Exception? failure = null;
            try { await new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None); }
            catch (FileNotFoundException exception) { failure = exception; }
            Require(failure is not null, "Preflight cleanup masked the original missing owned file error.");
            Require(File.ReadAllText(Path.Combine(fixture.Target, "a", "old-owned.dll")) == "nested original bytes"
                && AssemblyName.GetAssemblyName(Path.Combine(fixture.Target, "PulseReader.dll")).Version == new Version("1.0.0.0"), "Missing-file preflight changed original payload bytes.");
            Require(!Directory.EnumerateDirectories(fixture.Root, ".PulseReader-*").Any(), "Nested backup parents were left after missing-file preflight.");
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare(version: "2.0.0");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare();
            File.AppendAllText(prepared.AssetPath, "tampered");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare();
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared with
            { Offer = prepared.Offer with { TermsText = "different accepted terms" } }, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare(modify: package => package["distribution-terms.json"] = Encoding.UTF8.GetBytes(
                "{\"SchemaVersion\":1,\"TermsFileName\":\"Distribution-Terms.txt\",\"TermsSha256\":\"" + new string('0', 64) + "\"}"));
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare(extraEntries: Enumerable.Range(0, 4097).Select(n => ($"extra{n}.txt", "small", 0)).ToArray());
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare();
            var bytes = File.ReadAllBytes(prepared.AssetPath);
            for (var offset = 0; offset < bytes.Length - 24; offset++)
            {
                if (BitConverter.ToUInt32(bytes, offset) != 0x02014B50) continue;
                BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, offset + 24); break;
            }
            File.WriteAllBytes(prepared.AssetPath, bytes);
            prepared = prepared with { Offer = prepared.Offer with { Asset = prepared.Offer.Asset with
            { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } } };
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared, CancellationToken.None));
            fixture.RequireOriginal();
        }
        foreach (var malformedList in new[] { "PulseReader.exe\n../other.txt\n", "PulseReader.exe\npulsereader.exe\npayload-files.txt\ndependency-inventory.json\n", "PulseReader.exe\ndependency-inventory.json\n" })
        {
            using var fixture = new Fixture();
            File.WriteAllText(Path.Combine(fixture.Target, "payload-files.txt"), malformedList);
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            var prepared = fixture.Prepare();
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(prepared with { Installation = prepared.Installation with
            { ApplicationRoot = Path.GetPathRoot(fixture.Target)! } }, CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Target, "installation-scope.json"), "{\"Scope\":\"user\"}");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            File.AppendAllText(Path.Combine(fixture.Target, "payload-files.txt"), "config.json\n");
            File.WriteAllText(Path.Combine(fixture.Target, "config.json"), "settings");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
            fixture.RequireOriginal();
        }
        // Directory links are rejected while pinning ancestry, before any live mutation.
        using (var fixture = new Fixture())
        {
            var external = Path.Combine(fixture.Root, "external"); Directory.CreateDirectory(external);
            var link = Path.Combine(fixture.Target, "licenses");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, external }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Cannot start fixture junction helper.");
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            await process.WaitForExitAsync();
            Require(process.ExitCode == 0, "Cannot create fresh-temp junction fixture: " + output + error);
            try
            {
                await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
                fixture.RequireOriginal();
                Require(!File.Exists(Path.Combine(external, "new.txt")), "Update traversed a directory link.");
            }
            finally { Directory.Delete(link, recursive: false); }
        }
    }

    private static async Task RollbackAndCancellation()
    {
        using (var fixture = new Fixture())
        {
            var directoryPinned = false; var filePinned = false;
            await new PortableUpdateTransaction(point =>
            {
                if (point != "Prepared") return;
                try { Directory.Move(fixture.Target, fixture.Target + "-redirected"); }
                catch (IOException) { directoryPinned = true; }
                try { File.WriteAllText(Path.Combine(fixture.Target, "PulseReader.exe"), "concurrent edit"); }
                catch (IOException) { filePinned = true; }
            }).ApplyAsync(fixture.Prepare(), CancellationToken.None);
            Require(directoryPinned && filePinned, "Live directories or old payload files were not pinned during mutation.");
        }
        using (var fixture = new Fixture())
        {
            await Reject(() => new PortableUpdateTransaction(point =>
            {
                if (point == "Published:PulseReader.exe") throw new IOException("Injected publication failure.");
            }).ApplyAsync(fixture.Prepare(), CancellationToken.None));
            fixture.RequireOriginal();
            Require(!File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "A successful rollback retained its journal.");
        }
        using (var fixture = new Fixture())
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = false;
            try
            {
                await new PortableUpdateTransaction(point =>
                {
                    if (point == "Published:PulseReader.exe") cancellation.Cancel();
                }).ApplyAsync(fixture.Prepare(), cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Precommit cancellation was not reported."); fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        using (var cancellation = new CancellationTokenSource())
        {
            await new PortableUpdateTransaction(point =>
            {
                if (point == "Committed") cancellation.Cancel();
            }).ApplyAsync(fixture.Prepare(), cancellation.Token);
            Require(ReadVersion(fixture.Target) == "1.1.0", "Postcommit cancellation undid the installed update.");
        }
    }

    private static async Task Recovery()
    {
        foreach (var interruption in new[] { "Prepared", "BackedUp:dependency-inventory.json", "Published:PulseReader.exe", "Committed" })
        {
            using var fixture = new Fixture();
            await Interrupt(fixture, interruption);
            Require(File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "Interruption did not persist a journal.");
            await new PortableUpdateTransaction().RecoverAsync(fixture.Target);
            if (interruption == "Committed") Require(ReadVersion(fixture.Target) == "1.1.0", "Recovery undid a committed update.");
            else fixture.RequireOriginal();
            Require(!File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "Resolved interruption retained its journal.");
        }
        using (var fixture = new Fixture())
        {
            await Interrupt(fixture, "Published:PulseReader.exe");
            File.WriteAllText(Path.Combine(fixture.Target, "PulseReader.exe"), "later user edit");
            await Reject(() => new PortableUpdateTransaction().RecoverAsync(fixture.Target));
            Require(File.ReadAllText(Path.Combine(fixture.Target, "PulseReader.exe")) == "later user edit", "Recovery erased later edits.");
            Require(File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target))
                && Directory.EnumerateFiles(fixture.Root, "PulseReader.exe", SearchOption.AllDirectories).Any(p => p.Contains(".PulseReader-backup-")),
                "Conflicting recovery discarded its evidence or original bytes.");
            await Reject(() => new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None));
            Require(File.ReadAllText(Path.Combine(fixture.Target, "PulseReader.exe")) == "later user edit", "New apply ignored an unresolved recovery conflict.");
        }
        using (var fixture = new Fixture())
        {
            await Interrupt(fixture, "Published:PulseReader.exe");
            var live = Path.Combine(fixture.Target, "PulseReader.exe");
            File.Delete(live); File.WriteAllBytes(live, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PulseReader.exe")));
            await Reject(() => new PortableUpdateTransaction().RecoverAsync(fixture.Target));
            Require(File.Exists(live), "Recovery erased a replacement with different identity but identical bytes.");
        }
        using (var fixture = new Fixture())
        {
            await Interrupt(fixture, "Prepared");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await new PortableUpdateTransaction().RecoverAsync(fixture.Target, cancellation.Token); }
            catch (OperationCanceledException) { }
            fixture.RequireOriginal();
            Require(File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "Cancelled recovery discarded its journal.");
            await new PortableUpdateTransaction().RecoverAsync(fixture.Target);
            fixture.RequireOriginal();
        }
        using (var fixture = new Fixture())
        {
            await Interrupt(fixture, "Published:PulseReader.exe");
            await new PortableUpdateTransaction().ApplyAsync(fixture.Prepare(), CancellationToken.None);
            Require(ReadVersion(fixture.Target) == "1.1.0", "Apply did not recover a prior interrupted update before starting its own transaction.");
            Require(!File.Exists(PortableUpdateTransaction.JournalPath(fixture.Target)), "A recovered then applied update retained its journal.");
        }
    }

    private static async Task Interrupt(Fixture fixture, string interruption)
    {
        var interrupted = false;
        try
        {
            await new PortableUpdateTransaction(point =>
            {
                if (point == interruption) throw new PortableUpdateInterruptionException();
            }).ApplyAsync(fixture.Prepare(), CancellationToken.None);
        }
        catch (PortableUpdateInterruptionException) { interrupted = true; }
        Require(interrupted, "Fixture checkpoint was not reached: " + interruption);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or BadImageFormatException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or JsonException)
        { return; }
        throw new InvalidOperationException("Unsafe portable update unexpectedly succeeded.");
    }
    private static string? ReadVersion(string target)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "dependency-inventory.json")));
        return json.RootElement.GetProperty("AppVersion").GetString();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void RefreshList(Dictionary<string, byte[]> package) => package["payload-files.txt"] =
        Encoding.UTF8.GetBytes(string.Join('\n', package.Keys.Where(p => p != "payload-files.txt").Append("payload-files.txt")) + "\n");

    private static byte[] VersionedApplicationDll(string text)
    {
        // Copy the isolated build's actual application PE. Patch only test-copy assembly and
        // Win32 fixed version fields; never load or change the running assembly or installed files.
        var bytes = File.ReadAllBytes(typeof(PortableUpdateTransaction).Assembly.Location);
        var version = new Version(text);
        using (var pe = new PEReader(new MemoryStream(bytes, writable: false)))
        {
            var metadata = pe.GetMetadataReader();
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.Assembly) + 4;
            foreach (var (part, index) in new[] { version.Major, version.Minor, version.Build, 0 }.Select((part, index) => (part, index)))
                BitConverter.GetBytes(checked((ushort)part)).CopyTo(bytes, offset + index * 2);
        }
        var fixedInfoFound = false;
        for (var offset = 0; offset <= bytes.Length - 24; offset++)
        {
            if (BitConverter.ToUInt32(bytes, offset) != 0xFEEF04BD) continue;
            var high = checked((uint)((version.Major << 16) | version.Minor));
            var low = checked((uint)(version.Build << 16));
            BitConverter.GetBytes(high).CopyTo(bytes, offset + 8); BitConverter.GetBytes(low).CopyTo(bytes, offset + 12);
            BitConverter.GetBytes(high).CopyTo(bytes, offset + 16); BitConverter.GetBytes(low).CopyTo(bytes, offset + 20);
            fixedInfoFound = true; break;
        }
        Require(fixedInfoFound, "Isolated application DLL fixture has no fixed Win32 version information.");
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReader-portable-checks-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "app");
        private string Variant { get; }
        internal Fixture(string variant = "win-x64")
        {
            Variant = variant; Directory.CreateDirectory(Target);
            var old = Package("1.0.0", variant, false);
            foreach (var (path, bytes) in old)
            {
                var full = Path.Combine(Target, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes);
            }
        }
        internal PreparedUpdate Prepare(string? version = null, string? variant = null, (string Path, string Text, int Attributes)[]? extraEntries = null,
            Action<Dictionary<string, byte[]>>? modify = null)
        {
            var asset = Path.Combine(Root, "asset-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var file = File.Create(asset))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var package = Package(version ?? "1.1.0", variant ?? Variant, true); modify?.Invoke(package);
                foreach (var (path, bytes) in package)
                {
                    var entry = zip.CreateEntry(path); using var output = entry.Open(); output.Write(bytes);
                }
                foreach (var (path, text, attributes) in extraEntries ?? [])
                {
                    var entry = zip.CreateEntry(path); entry.ExternalAttributes = attributes;
                    using var output = entry.Open(); output.Write(Encoding.UTF8.GetBytes(text));
                }
            }
            var bytesOnDisk = File.ReadAllBytes(asset);
            var offer = new UpdateOffer(1, new Version("1.1.0"), new Uri("https://example.invalid/release"),
                new UpdateAsset(1, "fixture.zip", new Uri("https://example.invalid/fixture.zip"), bytesOnDisk.Length, Convert.ToHexString(SHA256.HashData(bytesOnDisk))),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture terms"))), "fixture terms");
            return new(new(new Version("1.0.0"), Variant, UpdatePackageKind.Portable, InstallationScope.User, Target), offer, Root, asset);
        }
        private static Dictionary<string, byte[]> Package(string version, string variant, bool newer)
        {
            var result = new Dictionary<string, byte[]>
            {
                ["PulseReader.exe"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PulseReader.exe")),
                ["PulseReader.dll"] = VersionedApplicationDll(newer ? "1.1.0" : "1.0.0"),
                ["PulseReader.deps.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PulseReader.deps.json")),
                ["PulseReader.runtimeconfig.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PulseReader.runtimeconfig.json")),
                ["Distribution-Terms.txt"] = Encoding.UTF8.GetBytes("fixture terms"),
                ["distribution-terms.json"] = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    SchemaVersion = 1, TermsFileName = "Distribution-Terms.txt",
                    TermsSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture terms")))
                }),
                ["dependency-inventory.json"] = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, AppVersion = version, Variant = variant })
            };
            result[newer ? "licenses/new.txt" : "obsolete.dll"] = Encoding.UTF8.GetBytes(newer ? "new nested license" : "obsolete original bytes");
            if (!newer) result["a/old-owned.dll"] = Encoding.UTF8.GetBytes("nested original bytes");
            RefreshList(result);
            return result;
        }
        internal void RequireOriginal()
        {
            Require(File.ReadAllBytes(Path.Combine(Target, "PulseReader.exe")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PulseReader.exe"))), "Original executable was not preserved/restored.");
            Require(File.ReadAllBytes(Path.Combine(Target, "PulseReader.dll")).SequenceEqual(VersionedApplicationDll("1.0.0")), "Original application DLL bytes were not preserved/restored.");
            Require(File.ReadAllText(Path.Combine(Target, "a", "old-owned.dll")) == "nested original bytes", "Nested original payload bytes were lost.");
            Require(File.ReadAllText(Path.Combine(Target, "obsolete.dll")) == "obsolete original bytes", "Original obsolete bytes were lost.");
            Require(ReadVersion(Target) == "1.0.0", "Original package inventory was not restored.");
        }
        public void Dispose()
        {
            // This is the fixture's own freshly created GUID temp root, never a real installation.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
