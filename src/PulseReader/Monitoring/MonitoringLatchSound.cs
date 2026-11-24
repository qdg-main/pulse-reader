using System.Diagnostics;
using NAudio.Wave;

namespace PulseReader.Monitoring;

internal static class MonitoringLatchSound
{
    internal const int SampleRate = 48000;
    internal const int LeadingSilenceMilliseconds = 30;
    internal const int ToneDurationMilliseconds = 270;
    internal const int TrailingSilenceMilliseconds = 60;
    internal const int DurationMilliseconds = LeadingSilenceMilliseconds
        + ToneDurationMilliseconds + TrailingSilenceMilliseconds;
    private static int _playing;

    public static void Play(bool attached = true)
    {
        if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                // Use our own quiet cue, not a Windows notification/system sound.
                // Keep device startup and the bounded wait off the UI thread.
                // The driver can report completion after Stop/Dispose. A task
                // completion source stays safe for that late callback.
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var samples = CreateSamples(attached);
                using var stream = new RawSourceWaveStream(samples, 0, samples.Length,
                    new WaveFormat(SampleRate, 16, 1));
                // Avoid very small buffers for a short cue: opening a device
                // while UIA/speech startup is busy can otherwise underrun it.
                using var output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
                output.PlaybackStopped += (_, _) => finished.TrySetResult();
                output.Init(stream);
                output.Play();
                finished.Task.Wait(TimeSpan.FromSeconds(2));
                output.Stop();
            }
            catch (Exception exception)
            {
                // Feedback is optional: an unavailable audio device must not
                // undo a successful latch or produce another error popup.
                Debug.WriteLine("Monitoring cue unavailable: " + exception.GetType().Name);
            }
            finally { Volatile.Write(ref _playing, 0); }
        });
    }

    internal static byte[] CreateSamples(bool attached = true)
    {
        var count = SampleRate * DurationMilliseconds / 1000;
        var offset = SampleRate * LeadingSilenceMilliseconds / 1000;
        var samples = new byte[count * sizeof(short)];
        var mixed = new double[count];
        // Approved listening pair 07: three separated, softly digital pulses.
        // Reverse the pitches, retaining the same rhythm, for detachment.
        AddNote(attached ? 440 : 739.99, startMilliseconds: 0, durationMilliseconds: 65);
        AddNote(554.37, startMilliseconds: 90, durationMilliseconds: 65);
        AddNote(attached ? 739.99 : 440, startMilliseconds: 180, durationMilliseconds: 90);
        // Match the peak normalization used by the approved browser preview.
        const double previewPeak = 0.10012699506382937;
        var scale = previewPeak / mixed.Max(value => Math.Abs(value));
        for (var index = 0; index < count; index++)
        {
            var value = (short)Math.Round(mixed[index] * scale * short.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                samples.AsSpan(index * sizeof(short)), value);
        }
        return samples;

        void AddNote(double frequency, int startMilliseconds, int durationMilliseconds)
        {
            var noteStart = offset + SampleRate * startMilliseconds / 1000;
            var noteCount = SampleRate * durationMilliseconds / 1000;
            for (var index = 0; index < noteCount; index++)
            {
                var progress = (double)index / (noteCount - 1);
                var envelope = Math.Pow(Math.Sin(Math.PI * progress), 2);
                var phase = 2 * Math.PI * frequency * index / SampleRate;
                var tone = Math.Sin(phase) - 0.09 * Math.Sin(3 * phase) + 0.025 * Math.Sin(5 * phase);
                mixed[noteStart + index] += envelope * tone;
            }
        }
    }
}
