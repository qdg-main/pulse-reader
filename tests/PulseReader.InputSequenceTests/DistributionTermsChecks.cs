using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Settings;
using PulseReader.Setup;

internal static class DistributionTermsChecks
{
    public static void Run()
    {
        CheckDevelopmentBypass();
        CheckAcceptanceAndUpgrades();
        CheckDeclineAndStartup();
        CheckInvalidPackages();
        CheckReceiptFailures();
        CheckDialog();
        Console.WriteLine("PASS: distribution terms gate validates packaged terms, requires explicit consent, preserves receipts across upgrades, fails closed, and supports accessible review/decline controls.");
    }

    private static void CheckDevelopmentBypass()
    {
        using var fixture = new Fixture();
        var result = fixture.Check();
        Require(result.CanStart && fixture.Prompts == 0 && fixture.Receipts.Reads == 0,
            "A development build should bypass distribution consent without touching receipts.");
        File.WriteAllText(Path.Combine(fixture.Root, "native-runtime-manifest.json"), "{}");
        RequireError(fixture, "A packaged marker without its consent contract must fail closed.");
    }

    private static void CheckAcceptanceAndUpgrades()
    {
        using var fixture = new Fixture();
        var firstSha = fixture.WritePackage("Fixture terms: first revision.\nAll terms remain visible.\n");
        Require(fixture.Check().CanStart && fixture.Prompts == 1 && fixture.Receipts.Writes == 1
            && fixture.Receipts.Accepted == firstSha && fixture.LastTerms?.Text.Contains("All terms remain visible.") == true,
            "First launch must show the complete terms and save their exact SHA256 after acceptance.");
        fixture.Receipts.Accepted = firstSha.ToLowerInvariant();
        Require(fixture.Check().CanStart && fixture.Prompts == 1 && fixture.Receipts.Writes == 1,
            "An accepted checksum must avoid repeat prompts, including lowercase installer receipts.");
        File.WriteAllText(Path.Combine(fixture.Root, "native-runtime-manifest.json"), "{\"flavour\":\"changed\"}");
        Require(fixture.Check().CanStart && fixture.Prompts == 1,
            "Changing the package flavour must preserve acceptance of unchanged terms.");
        var revisedSha = fixture.WritePackage("Fixture terms: second revision.\n");
        Require(fixture.Check().CanStart && fixture.Prompts == 2 && fixture.Receipts.Writes == 2
            && fixture.Receipts.Accepted == revisedSha && revisedSha != firstSha,
            "Changed terms must require new acceptance and replace the receipt only afterward.");
        File.AppendAllText(Path.Combine(fixture.Root, DistributionTermsConsent.TermsFileName), "Tampered.");
        RequireError(fixture, "An existing receipt must not bypass terms integrity verification.");
    }

    private static void CheckDeclineAndStartup()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("Fixture distribution terms.\n");
        var configuration = Path.Combine(fixture.Root, "config-fixture.json");
        File.WriteAllText(configuration, "unchanged configuration fixture");
        fixture.Accept = false;
        fixture.Receipts.Accepted = new string('A', 64);
        var priorReceipt = fixture.Receipts.Accepted;
        Require(fixture.Check().Status == DistributionTermsConsentStatus.Declined
            && fixture.Receipts.Writes == 0 && fixture.Receipts.Accepted == priorReceipt
            && File.ReadAllText(configuration) == "unchanged configuration fixture",
            "Declining must leave the prior receipt and configuration untouched.");
        var prompts = fixture.Prompts;
        var startup = fixture.Check(isStartup: true);
        Require(startup.Status == DistributionTermsConsentStatus.ManualAcceptanceRequired
            && startup.Error?.Contains("manually", StringComparison.Ordinal) == true
            && fixture.Prompts == prompts && fixture.Receipts.Writes == 0,
            "Startup must not prompt or silently accept unaccepted terms.");
        fixture.Accept = true;
        Require(fixture.Check().CanStart && fixture.Check(isStartup: true).CanStart,
            "Startup may proceed only after interactive acceptance of the current terms.");
    }

    private static void CheckInvalidPackages()
    {
        using var fixture = new Fixture();
        var sha = fixture.WritePackage("Fixture distribution terms.\n");
        File.Delete(Path.Combine(fixture.Root, DistributionTermsConsent.TermsFileName));
        RequireError(fixture, "Missing terms must block application startup.");
        fixture.WritePackage("Fixture distribution terms.\n");
        File.WriteAllText(fixture.ContractPath, "{ malformed JSON");
        RequireError(fixture, "Malformed JSON must block application startup.");

        foreach (var filename in new[] { "../Distribution-Terms.txt", "..\\Distribution-Terms.txt",
            "C:\\outside\\terms.txt", "sub/Distribution-Terms.txt", "distribution-terms.txt" })
        {
            fixture.WriteContract(sha, filename);
            RequireError(fixture, "Terms filenames must match the fixed packaged filename.");
        }
        fixture.WriteContract(sha, schema: 2);
        RequireError(fixture, "Unknown contract schema must block application startup.");
        fixture.WriteContract(new string('Z', 64));
        RequireError(fixture, "Nonhexadecimal checksum must block application startup.");
        fixture.WriteContract("ABCD");
        RequireError(fixture, "Short checksum must block application startup.");
        fixture.WriteContract(new string('0', 64));
        RequireError(fixture, "Tampered checksum must block application startup.");
        File.WriteAllText(fixture.ContractPath,
            $"{{\"SchemaVersion\":1,\"TermsFileName\":\"Distribution-Terms.txt\",\"TermsSha256\":\"{sha}\",\"TermsSha256\":\"{sha}\"}}");
        RequireError(fixture, "Duplicate contract properties must be rejected.");
        fixture.WriteContract(sha);
        File.AppendAllText(fixture.ContractPath, new string(' ', DistributionTermsConsent.MaximumContractBytes));
        RequireError(fixture, "Oversized contracts must be rejected before parsing.");
        fixture.WritePackage(new string('x', DistributionTermsConsent.MaximumTermsBytes + 1));
        RequireError(fixture, "Oversized terms must be rejected before showing a dialog.");
        fixture.WritePackage("  \r\n\t");
        RequireError(fixture, "Whitespace-only terms must be rejected.");
        fixture.WritePackage("Fixture\0hidden terms.");
        RequireError(fixture, "Terms containing a NUL must be rejected.");
        var invalidUtf8 = new byte[] { 0xC3, 0x28 };
        File.WriteAllBytes(Path.Combine(fixture.Root, DistributionTermsConsent.TermsFileName), invalidUtf8);
        fixture.WriteContract(Convert.ToHexString(SHA256.HashData(invalidUtf8)));
        RequireError(fixture, "Invalid UTF-8 must be rejected rather than replacing characters.");
        var bomTerms = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Complete BOM fixture terms.")).ToArray();
        File.WriteAllBytes(Path.Combine(fixture.Root, DistributionTermsConsent.TermsFileName), bomTerms);
        fixture.WriteContract(Convert.ToHexString(SHA256.HashData(bomTerms)));
        Require(fixture.Check().CanStart && fixture.LastTerms?.Text == "Complete BOM fixture terms.",
            "UTF-8 BOM must be included in the checksum and omitted from displayed text.");
    }

    private static void CheckReceiptFailures()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("Fixture distribution terms.\n");
        fixture.Receipts.FailRead = true;
        RequireError(fixture, "Receipt read failures must block application startup without prompting.");
        fixture.Receipts.FailRead = false;
        fixture.Receipts.FailWrite = true;
        var result = fixture.Check();
        Require(result.Status == DistributionTermsConsentStatus.Error && fixture.Prompts == 1
            && fixture.Receipts.Accepted is null && result.Error?.Contains("receipt", StringComparison.Ordinal) == true,
            "Receipt write failures must block startup after consent and report an actionable error.");
    }

    private static void CheckDialog()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var completeText = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"Complete fixture terms line {i}."));
                foreach (var isUpdate in new[] { false, true })
                foreach (var action in new[] { "accept", "decline", "close" })
                {
                    using var form = new DistributionTermsForm(completeText, isUpdate)
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-10000, -10000), ShowInTaskbar = false
                    };
                    Exception? dialogFailure = null;
                    form.Shown += (_, _) =>
                    {
                        try
                        {
                            var terms = form.Controls.OfType<RichTextBox>().Single();
                            var agree = form.Controls.OfType<CheckBox>().Single();
                            var proceed = form.Controls.OfType<GlowButton>().Single(b => b.Name == "AcceptDistributionTerms");
                            var decline = form.Controls.OfType<GlowButton>().Single(b => b.Name == "DeclineDistributionTerms");
                            var expectedDecline = isUpdate ? "Cancel update" : "Decline and exit";
                            Require(decline.Text == expectedDecline && decline.AccessibleName == expectedDecline,
                                "Visible and accessible decline labels must distinguish cancelling an update from exiting first launch.");
                            var description = form.Controls.OfType<Label>().Single().Text;
                            Require(isUpdate
                                ? description == "Review the distribution terms for this update. Cancelling leaves your current version running."
                                : description.Contains("continue using this packaged copy", StringComparison.Ordinal),
                                "Terms instructions must describe the update or first-launch action correctly.");
                            Require(terms.Text == completeText && terms.ReadOnly
                                && terms.ScrollBars == RichTextBoxScrollBars.Vertical && !terms.DetectUrls,
                                "The form must show all terms in a read-only scrollable control.");
                            Require(!agree.Checked && !proceed.Enabled && form.AcceptButton == proceed
                                && form.CancelButton == decline
                                && new Control[] { form, terms, agree, proceed, decline }.All(c => !string.IsNullOrEmpty(c.AccessibleName)),
                                "The form must start unaccepted with disabled Continue and named accessible controls.");
                            proceed.PerformClick();
                            Require(form.DialogResult != DialogResult.OK, "Disabled Continue must not accept terms.");
                            agree.Checked = true;
                            Require(proceed.Enabled, "Selecting agreement must enable Continue.");
                            agree.Checked = false;
                            Require(!proceed.Enabled, "Clearing agreement must disable Continue again.");
                            if (action == "accept") { agree.Checked = true; proceed.PerformClick(); }
                            else if (action == "decline") decline.PerformClick();
                            else form.Close();
                        }
                        catch (Exception exception) { dialogFailure = exception; form.Close(); }
                    };
                    var result = form.ShowDialog();
                    if (dialogFailure is not null) throw dialogFailure;
                    Require(result == (action == "accept" ? DialogResult.OK : DialogResult.Cancel),
                        "Only explicitly agreeing and clicking Continue may return acceptance.");
                }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("Distribution terms form checks did not finish.");
        if (failure is not null) throw new InvalidOperationException("Distribution terms form check failed.", failure);
    }

    private static void RequireError(Fixture fixture, string message)
    {
        var prompts = fixture.Prompts;
        var writes = fixture.Receipts.Writes;
        var result = fixture.Check();
        Require(result.Status == DistributionTermsConsentStatus.Error && !result.CanStart
            && !string.IsNullOrWhiteSpace(result.Error) && fixture.Prompts == prompts && fixture.Receipts.Writes == writes,
            message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReader-distribution-terms-" + Guid.NewGuid().ToString("N"));
        internal string ContractPath => Path.Combine(Root, DistributionTermsConsent.ContractFileName);
        internal FakeReceipts Receipts { get; } = new();
        internal bool Accept { get; set; } = true;
        internal int Prompts { get; private set; }
        internal DistributionTermsDocument? LastTerms { get; private set; }

        internal Fixture() => Directory.CreateDirectory(Root);

        internal string WritePackage(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(Path.Combine(Root, DistributionTermsConsent.TermsFileName), bytes);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            WriteContract(sha);
            return sha;
        }

        internal void WriteContract(string sha, string filename = DistributionTermsConsent.TermsFileName, int schema = 1) =>
            File.WriteAllText(ContractPath, JsonSerializer.Serialize(new
            {
                SchemaVersion = schema, TermsFileName = filename, TermsSha256 = sha
            }));

        internal DistributionTermsConsentResult Check(bool isStartup = false) =>
            DistributionTermsConsent.Check(Root, isStartup, Receipts, terms =>
            {
                Prompts++;
                LastTerms = terms;
                return Accept;
            });

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeReceipts : IDistributionTermsReceiptStore
    {
        internal string? Accepted { get; set; }
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }

        public string? ReadAcceptedSha256()
        {
            Reads++;
            if (FailRead) throw new UnauthorizedAccessException("Fixture receipt read denied.");
            return Accepted;
        }

        public void WriteAcceptedSha256(string sha256)
        {
            if (FailWrite) throw new IOException("Fixture receipt write denied.");
            Writes++;
            Accepted = sha256;
        }
    }
}
