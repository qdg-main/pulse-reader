using System.Buffers.Binary;
using System.Security.Cryptography;
using PulseReader.Monitoring;

internal static class MonitoringLatchSoundChecks
{
    public static void Run()
    {
        CheckCue(attached: true);
        CheckCue(attached: false);
        if (MonitoringLatchSound.CreateSamples().SequenceEqual(MonitoringLatchSound.CreateSamples(attached: false)))
            throw new InvalidOperationException("Attach and detach cues are indistinguishable.");
        Console.WriteLine("PASS: attach/detach cues are distinct, quiet, padded, and fade to silence.");
    }

    private static void CheckCue(bool attached)
    {
        var samples = MonitoringLatchSound.CreateSamples(attached);
        // Captured from the user-approved pair 07 in the listening corpus.
        var approvedHash = attached
            ? "6592E52D8B2FE6A6250310AFDF734B9BF5B57B8787342333418055A75676870B"
            : "949B19B0C24AECCAAC7BAEF95C8C9830DBAA67CF5B56054086B87E3F5BF7F8EA";
        if (Convert.ToHexString(SHA256.HashData(samples)) != approvedHash)
            throw new InvalidOperationException("Monitoring cue differs from approved listening pair 07.");
        var expectedCount = MonitoringLatchSound.SampleRate * MonitoringLatchSound.DurationMilliseconds / 1000;
        if (samples.Length != expectedCount * sizeof(short))
            throw new InvalidOperationException("Latch cue has the wrong duration or PCM format.");
        var peak = 0;
        var previous = 0;
        var maximumStep = short.MaxValue * 0.13 * 2 * Math.PI * 1000 * 1.5 / MonitoringLatchSound.SampleRate + 4;
        for (var index = 0; index < expectedCount; index++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(index * 2));
            peak = Math.Max(peak, Math.Abs((int)value));
            if (Math.Abs(value - previous) > maximumStep)
                throw new InvalidOperationException("Monitoring cue contains a discontinuity or click impulse.");
            previous = value;
        }
        if (peak < short.MaxValue * 0.075 || peak > short.MaxValue * 0.13)
            throw new InvalidOperationException("Latch cue is silent or exceeds its quiet amplitude limit.");
        if (BinaryPrimitives.ReadInt16LittleEndian(samples) != 0
            || BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(samples.Length - 2)) != 0)
            throw new InvalidOperationException("Latch cue does not fade to silence at both boundaries.");
        // Give the output device time to settle before the audible envelope,
        // and drain silence rather than stopping immediately after the tone.
        var paddingBytes = MonitoringLatchSound.SampleRate * 20 / 1000 * sizeof(short);
        if (samples.Take(paddingBytes).Any(value => value != 0)
            || samples.TakeLast(paddingBytes).Any(value => value != 0))
            throw new InvalidOperationException("Monitoring cue has no device-start/drain silence padding.");
        foreach (var gap in new[] { (Start: 65, End: 90), (Start: 155, End: 180) })
        {
            var start = MonitoringLatchSound.SampleRate * (MonitoringLatchSound.LeadingSilenceMilliseconds + gap.Start) / 1000;
            var count = MonitoringLatchSound.SampleRate * (gap.End - gap.Start) / 1000;
            if (samples.AsSpan(start * 2, count * 2).ContainsAnyExcept((byte)0))
                throw new InvalidOperationException("Monitoring pulse signature lost its quiet inter-note gaps.");
        }
        var toneStart = MonitoringLatchSound.SampleRate * MonitoringLatchSound.LeadingSilenceMilliseconds / 1000;
        var toneCount = MonitoringLatchSound.SampleRate * MonitoringLatchSound.ToneDurationMilliseconds / 1000;
        var early = CountCrossings(samples, toneStart + toneCount / 8, toneCount / 4);
        var late = CountCrossings(samples, toneStart + toneCount * 5 / 8, toneCount / 4);
        if (attached ? late <= early : late >= early)
            throw new InvalidOperationException("Monitoring chime notes move in the wrong direction.");
    }

    private static int CountCrossings(byte[] samples, int start, int count)
    {
        var previous = 0;
        var crossings = 0;
        for (var index = start; index < start + count; index++)
        {
            var value = Math.Sign(BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(index * 2)));
            if (value == 0) continue;
            if (previous != 0 && value != previous) crossings++;
            previous = value;
        }
        return crossings;
    }
}
