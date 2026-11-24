using PulseReader.Monitoring;
using UIA = Interop.UIAutomationClient;

internal static class DocumentMonitoringFallbackChecks
{
    public static void Run()
    {
        using var ready = new ManualResetEventSlim();
        Form? form = null;
        RichTextBox? editor = null;
        Exception? failure = null;
        var handle = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            try
            {
                form = new Form { Text = "Pulse Reader document fallback regression", Width = 480, Height = 180, ShowInTaskbar = false };
                editor = new RichTextBox { Dock = DockStyle.Fill, Text = "Synthetic private draft must stay silent.", ReadOnly = false };
                form.Controls.Add(editor);
                form.Shown += (_, _) => { handle = form.Handle; ready.Set(); };
                Application.Run(form);
            }
            catch (Exception exception) { failure = exception; ready.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Document fixture startup timed out.");
            if (failure is not null) throw new InvalidOperationException("Document fixture startup failed.", failure);
            using var fingerprints = new WindowTextFingerprinter();
            var automation = new UIA.CUIAutomation8();
            WindowTextSnapshot Read() => WindowTextSnapshotReader.Read(automation, automation.ElementFromHandle(handle), fingerprints);
            Assert(Read().Blocks.Count == 0, "Editable document fallback exposed an unsent draft.");
            Change(() => editor!.Text = "Newly typed private content must also stay silent.");
            Assert(Read().Blocks.Count == 0, "Editable document fallback exposed later typing.");
            Change(() => editor!.ReadOnly = true);
            Assert(Read().Blocks.Any(b => b.Text.Contains("Newly typed private content", StringComparison.Ordinal)),
                "Guarding editable documents disabled legitimate read-only document fallback.");
            Change(() => editor!.Visible = false);
            Assert(Read().Blocks.Count == 0, "A hidden document was included by fallback capture.");
            Console.WriteLine("PASS: document fallback excludes editable/hidden content and preserves read-only documents.");
        }
        finally
        {
            if (form is not null && form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(() => form.Close());
            thread.Join(TimeSpan.FromSeconds(3));
        }
        void Change(Action action)
        {
            using var changed = new ManualResetEventSlim();
            form!.BeginInvoke(() => { action(); changed.Set(); });
            if (!changed.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Document fixture update timed out.");
        }
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
