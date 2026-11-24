internal static class UpdateQualificationPayloadChecks
{
    internal static void Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PulseReader-qualification-payload-" + Guid.NewGuid().ToString("N"));
        var expected = Path.Combine(fixture, "expected");
        var actual = Path.Combine(fixture, "actual");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(actual);
        try
        {
            foreach (var root in new[] { expected, actual })
            {
                File.WriteAllText(Path.Combine(root, "payload-files.txt"), "payload-files.txt\nPulseReader.dll\n");
                File.WriteAllText(Path.Combine(root, "PulseReader.dll"), "Expected fixture bytes.");
            }
            if (UpdateMachineElevationQualification.VerifyPayload(actual, expected).Count != 2)
                throw new InvalidOperationException("Complete identical fixture payload was rejected.");

            // A damaged ownership list must not hide a missing expected file.
            File.Delete(Path.Combine(actual, "PulseReader.dll"));
            File.WriteAllText(Path.Combine(actual, "payload-files.txt"), "");
            Rejects(() => UpdateMachineElevationQualification.VerifyPayload(actual, expected), "Shortened ownership list passed qualification.");

            File.Copy(Path.Combine(expected, "payload-files.txt"), Path.Combine(actual, "payload-files.txt"), true);
            Rejects(() => UpdateMachineElevationQualification.VerifyPayload(actual, expected), "Missing owned file passed qualification.");
            File.WriteAllText(Path.Combine(actual, "PulseReader.dll"), "Corrupt fixture bytes.");
            Rejects(() => UpdateMachineElevationQualification.VerifyPayload(actual, expected), "Changed owned file passed qualification.");
            Console.WriteLine("PASS: machine qualification requires the complete expected ownership list and exact payload bytes.");
        }
        finally
        {
            // Only the two known fixture trees and their exact files are removed.
            foreach (var root in new[] { expected, actual })
            {
                foreach (var name in new[] { "payload-files.txt", "PulseReader.dll" }) File.Delete(Path.Combine(root, name));
                Directory.Delete(root);
            }
            Directory.Delete(fixture);
        }
    }

    private static void Rejects(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
