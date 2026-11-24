using System.Threading.Channels;
using PulseReader.Monitoring;
using PulseReader.Selection;
using UIA = Interop.UIAutomationClient;

internal static class WindowMonitoringSmokeChecks
{
    private const string FirstSentence = "The first streamed sentence arrived.";
    private const string SecondSentence = "The second streamed sentence arrived.";
    private const string ListItemText = "Response tracking. The explanatory list body must be captured once.";

    public static async Task RunAsync()
    {
        using var providerReady = new ManualResetEventSlim();
        Form? providerForm = null;
        Label? streamLabel = null;
        Label? submittedLabel = null;
        TextBox? editableText = null;
        Exception? providerError = null;
        var sourceWindow = IntPtr.Zero;

        var providerThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                providerForm = new Form
                {
                    ClientSize = new Size(620, 240),
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.CenterScreen,
                    Text = "Pulse Reader streaming accessibility provider"
                };
                var baseline = new Label
                {
                    AutoSize = true,
                    Location = new Point(18, 18),
                    Text = "Existing text must remain part of the baseline."
                };
                streamLabel = new Label
                {
                    AutoSize = true,
                    Location = new Point(18, 62),
                    Text = string.Empty
                };
                submittedLabel = new Label
                {
                    AutoSize = true,
                    Location = new Point(18, 84),
                    Text = string.Empty
                };
                editableText = new TextBox
                {
                    Location = new Point(18, 126),
                    Size = new Size(580, 24),
                    Text = "Editable baseline text must never be monitored."
                };
                providerForm.Controls.Add(baseline);
                providerForm.Controls.Add(streamLabel);
                providerForm.Controls.Add(submittedLabel);
                providerForm.Controls.Add(editableText);
                var listItem = new Panel
                {
                    AccessibleRole = AccessibleRole.ListItem,
                    AccessibleName = ListItemText,
                    Location = new Point(18, 170),
                    Size = new Size(580, 42)
                };
                listItem.Controls.Add(new Label { Text = "Response tracking.", AutoSize = true });
                providerForm.Controls.Add(listItem);
                var toolbar = new Panel
                {
                    AccessibleRole = AccessibleRole.ToolBar,
                    Location = new Point(18, 215),
                    Size = new Size(580, 22)
                };
                toolbar.Controls.Add(new Label { Text = "Navigation status must stay silent.", AutoSize = true });
                providerForm.Controls.Add(toolbar);
                providerForm.Shown += (_, _) =>
                {
                    sourceWindow = providerForm.Handle;
                    providerReady.Set();
                };
                Application.Run(providerForm);
            }
            catch (Exception exception)
            {
                providerError = exception;
                providerReady.Set();
            }
        })
        {
            IsBackground = true,
            Name = "PulseReader.WindowMonitoringSmokeProvider"
        };
        providerThread.SetApartmentState(ApartmentState.STA);
        providerThread.Start();

        try
        {
            if (!providerReady.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    "The window-monitoring smoke provider did not become ready.");
            }

            if (providerError is not null)
            {
                throw new InvalidOperationException(
                    "The window-monitoring smoke provider failed.",
                    providerError);
            }

            var captureAutomation = new UIA.CUIAutomation8();
            using (var captureFingerprinter = new WindowTextFingerprinter())
            {
                var captured = WindowTextSnapshotReader.Read(captureAutomation,
                    captureAutomation.ElementFromHandle(sourceWindow), captureFingerprinter);
                if (captured.Blocks.Count(block => block.Text == ListItemText) != 1
                    || captured.Blocks.Any(block => block.Text == "Response tracking."
                        || block.Text.Contains("Navigation status")))
                    throw new InvalidOperationException("Native list-item capture lost the body or repeated its child heading.");
            }

            using var session = new WindowTextMonitoringSession(sourceWindow);
            var observed = Channel.CreateUnbounded<string>();
            session.TextAvailable += (_, eventArgs) => observed.Writer.TryWrite(eventArgs.Text);
            var startResult = await session.StartAsync(CancellationToken.None);
            if (!startResult.Started)
            {
                throw new InvalidOperationException(
                    $"The live monitoring session did not start: {startResult.Error}");
            }

            providerForm!.BeginInvoke(() => streamLabel!.Text = FirstSentence);
            var first = await observed.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            providerForm.BeginInvoke(
                () => editableText!.Text = "Changed editable text must still be ignored.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (observed.Reader.TryRead(out var editableResult))
            {
                throw new InvalidOperationException(
                    $"Live monitoring emitted editable text as '{editableResult}'.");
            }

            const string submittedText = "A quickly submitted editor message must remain silent.";
            providerForm.BeginInvoke(
                () =>
                {
                    providerForm.Activate();
                    NativeInput.RestoreForegroundWindow(providerForm.Handle);
                    editableText!.Focus();
                    editableText.Text = submittedText;
                    editableText.SelectionStart = editableText.TextLength;
                });
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            var automation = new UIA.CUIAutomation8();
            var sourceElement = automation.ElementFromHandle(sourceWindow)
                ?? throw new InvalidOperationException(
                    "The smoke provider disappeared before the editable probe check.");
            var editorElement = sourceElement.FindFirst(UIA.TreeScope.TreeScope_Descendants,
                automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA.UIA_ControlTypeIds.UIA_EditControlTypeId));
            editorElement?.SetFocus();
            using (var fingerprinter = new WindowTextFingerprinter())
            {
                var focusedEditable = WindowTextSnapshotReader.TryReadFocusedEditableFingerprints(
                    automation,
                    sourceElement,
                    fingerprinter);
                if (focusedEditable is null || !focusedEditable.Any(value =>
                        value.Hash == fingerprinter.Create(submittedText).Hash))
                {
                    throw new InvalidOperationException(
                        "The focused editable probe did not capture the smoke provider text.");
                }
            }

            // Reading the editor from this test does not prove that the
            // monitoring worker has observed it. Synchronize with its next
            // probe instead of racing a fixed 400 ms delay against UIA work.
            var observationVersion = session.EditableObservationVersion;
            var observationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (session.EditableObservationVersion == observationVersion
                   && DateTimeOffset.UtcNow < observationDeadline)
                await Task.Delay(20);
            if (session.EditableObservationVersion == observationVersion)
                throw new InvalidOperationException($"The monitoring worker did not observe the focused editor before the submission test (source foreground: {NativeInput.GetForegroundWindowHandle() == sourceWindow}; observation version: {observationVersion}).");

            providerForm.BeginInvoke(
                () =>
                {
                    editableText!.Clear();
                    submittedLabel!.Text = submittedText;
                });
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (observed.Reader.TryRead(out var submittedResult))
            {
                throw new InvalidOperationException(
                    $"Live monitoring repeated submitted editor text as '{submittedResult}'.");
            }

            providerForm.BeginInvoke(
                () => streamLabel!.Text = $"{FirstSentence} {SecondSentence}");
            var second = await observed.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));

            if (first != FirstSentence || second != SecondSentence)
            {
                throw new InvalidOperationException(
                    $"Live monitoring returned '{first}' and '{second}' instead of the two appended sentences.");
            }

            Console.WriteLine(
                "PASS: live Windows accessibility monitoring ignored its baseline and submitted editor text, then emitted two appended sentences exactly once.");
        }
        finally
        {
            if (providerForm is not null && providerForm.IsHandleCreated)
            {
                providerForm.BeginInvoke(providerForm.Close);
            }

            if (!providerThread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    "The window-monitoring smoke provider did not close cleanly.");
            }
        }
    }

    public static async Task RunManualProviderAsync()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? providerError = null;
        var providerThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new Form
                {
                    ClientSize = new Size(720, 400),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    StartPosition = FormStartPosition.CenterScreen,
                    Text = "Pulse Reader window-monitoring review"
                };
                var instructions = new Label
                {
                    AutoSize = false,
                    Font = new Font("Segoe UI", 10f),
                    Location = new Point(18, 16),
                    Size = new Size(684, 62),
                    Text = "Select the opening text, then hold Pulse Reader's hotkey for about 650 ms. "
                           + "Pulse Reader should read the selection, ignore this existing text, and then read each new sentence below."
                };
                var initialSelection = new TextBox
                {
                    Font = new Font("Segoe UI", 10f),
                    Location = new Point(18, 88),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Size = new Size(684, 72),
                    Text = "This selected opening passage should be spoken first. It should not be repeated by the monitor."
                };
                var streamHeading = new Label
                {
                    AutoSize = true,
                    Font = new Font("Segoe UI Semibold", 10f),
                    Location = new Point(18, 180),
                    Text = "Live output"
                };
                var streamLabel = new Label
                {
                    AutoEllipsis = false,
                    AutoSize = false,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Segoe UI", 10f),
                    Location = new Point(18, 206),
                    Size = new Size(684, 94),
                    Text = "Waiting for new text."
                };
                var appendButton = new Button
                {
                    Location = new Point(18, 320),
                    Size = new Size(190, 34),
                    Text = "Append next sentence"
                };
                var automaticButton = new Button
                {
                    Location = new Point(218, 320),
                    Size = new Size(190, 34),
                    Text = "Start automatic stream"
                };
                var sentences = new[]
                {
                    "The first complete streamed sentence has arrived.",
                    "A second sentence follows without repeating the first one.",
                    "The monitor waits for punctuation before speaking a changing line.",
                    "This final sentence confirms that the stream remains attached."
                };
                var nextSentence = 0;
                var automaticTimer = new System.Windows.Forms.Timer { Interval = 240 };
                var automaticWords = Array.Empty<string>();
                var automaticWord = 0;
                var settledPrefix = streamLabel.Text;

                void AppendNextSentence()
                {
                    if (nextSentence >= sentences.Length)
                    {
                        form.Text = "Pulse Reader window-monitoring review - stream complete";
                        automaticTimer.Stop();
                        automaticButton.Text = "Stream complete";
                        automaticButton.Enabled = false;
                        appendButton.Enabled = false;
                        return;
                    }

                    var sentence = sentences[nextSentence++];
                    streamLabel.Text = $"{streamLabel.Text} {sentence}";
                    settledPrefix = streamLabel.Text;
                    form.Text = $"Pulse Reader window-monitoring review - appended {nextSentence} of {sentences.Length}";
                }

                appendButton.Click += (_, _) => AppendNextSentence();
                automaticButton.Click += (_, _) =>
                {
                    if (automaticTimer.Enabled)
                    {
                        automaticTimer.Stop();
                        automaticButton.Text = "Resume automatic stream";
                        return;
                    }

                    if (nextSentence >= sentences.Length)
                    {
                        return;
                    }

                    automaticWords = sentences[nextSentence].Split(' ');
                    automaticWord = 0;
                    settledPrefix = streamLabel.Text;
                    automaticButton.Text = "Pause automatic stream";
                    automaticTimer.Start();
                };
                automaticTimer.Tick += (_, _) =>
                {
                    if (automaticWord < automaticWords.Length)
                    {
                        var partial = string.Join(' ', automaticWords.Take(automaticWord + 1));
                        streamLabel.Text = $"{settledPrefix} {partial}";
                        automaticWord++;
                        return;
                    }

                    nextSentence++;
                    settledPrefix = streamLabel.Text;
                    automaticTimer.Stop();
                    form.Text = $"Pulse Reader window-monitoring review - streamed {nextSentence} of {sentences.Length}";
                    automaticButton.Text = "Start automatic stream";
                    if (nextSentence >= sentences.Length)
                    {
                        automaticButton.Text = "Stream complete";
                        automaticButton.Enabled = false;
                        appendButton.Enabled = false;
                    }
                };
                form.FormClosed += (_, _) =>
                {
                    automaticTimer.Stop();
                    automaticTimer.Dispose();
                    closed.TrySetResult();
                };
                form.Controls.AddRange(
                    [
                        instructions,
                        initialSelection,
                        streamHeading,
                        streamLabel,
                        appendButton,
                        automaticButton
                    ]);
                initialSelection.SelectAll();
                Application.Run(form);
            }
            catch (Exception exception)
            {
                providerError = exception;
                closed.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "PulseReader.WindowMonitoringManualProvider"
        };
        providerThread.SetApartmentState(ApartmentState.STA);
        providerThread.Start();

        await closed.Task;
        providerThread.Join(TimeSpan.FromSeconds(2));
        if (providerError is not null)
        {
            throw new InvalidOperationException(
                "The manual window-monitoring provider failed.",
                providerError);
        }
    }
}
