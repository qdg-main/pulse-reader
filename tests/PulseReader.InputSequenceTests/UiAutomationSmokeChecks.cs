using PulseReader.Selection;
using System.Runtime.InteropServices;

internal static class UiAutomationSmokeChecks
{
    private const int SwHide = 0;
    private const string SourceText =
        "Text before the selection. UI Automation selected this exact sentence. Text after it.";
    private const string ExpectedSelection =
        "UI Automation selected this exact sentence.";

    public static Task RunManualProviderAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using var form = new Form
                {
                    ClientSize = new Size(680, 270),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    StartPosition = FormStartPosition.CenterScreen,
                    Text = "Pulse Reader - UI Automation fallback test"
                };
                using var instructions = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 92,
                    Padding = new Padding(14, 14, 14, 8),
                    Text = "Select any text below and press Pulse Reader's normal hotkey.\r\n"
                           + "This field deliberately refuses clipboard copy commands, so successful speech confirms the UI Automation fallback."
                };
                using var textBox = new CopyBlockingTextBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 12F),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Text = "Pulse Reader recovered this selected sentence without using the clipboard.\r\n\r\n"
                           + "You can also select a smaller phrase or part of this second paragraph. The normal copy route remains blocked throughout this window."
                };

                form.Controls.Add(textBox);
                form.Controls.Add(instructions);
                form.Shown += (_, _) =>
                {
                    form.Activate();
                    textBox.Focus();
                    const string initialSelection =
                        "Pulse Reader recovered this selected sentence without using the clipboard.";
                    textBox.Select(0, initialSelection.Length);
                    HidePrivateConsoleWindow();
                };
                form.FormClosed += (_, _) => completion.TrySetResult();
                Application.Run(form);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "PulseReader.UiAutomationManualProvider"
        };
        providerThread.SetApartmentState(ApartmentState.STA);
        providerThread.Start();
        return completion.Task;
    }

    public static async Task RunAsync()
    {
        using var providerReady = new ManualResetEventSlim();
        Form? providerForm = null;
        Exception? providerError = null;
        var sourceWindow = IntPtr.Zero;
        var sourceControlWindow = IntPtr.Zero;

        var providerThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                providerForm = new Form
                {
                    ClientSize = new Size(520, 110),
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.CenterScreen,
                    Text = "Pulse Reader UI Automation smoke provider",
                    TopMost = true
                };
                var textBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    Text = SourceText
                };
                providerForm.Controls.Add(textBox);
                providerForm.Shown += (_, _) =>
                {
                    providerForm.Activate();
                    textBox.Focus();
                    var selectionStart = SourceText.IndexOf(
                        ExpectedSelection,
                        StringComparison.Ordinal);
                    textBox.Select(selectionStart, ExpectedSelection.Length);
                    sourceWindow = providerForm.Handle;
                    sourceControlWindow = textBox.Handle;
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
            Name = "PulseReader.UiAutomationSmokeProvider"
        };
        providerThread.SetApartmentState(ApartmentState.STA);
        providerThread.Start();

        try
        {
            if (!providerReady.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    "The UI Automation smoke provider did not become ready.");
            }

            if (providerError is not null)
            {
                throw new InvalidOperationException(
                    "The UI Automation smoke provider failed.",
                    providerError);
            }

            var reader = new WindowsUiAutomationSelectionReader(() => sourceWindow);
            var automationFallback = new UiAutomationSelectedTextFallback(
                reader,
                TimeSpan.FromSeconds(1));
            var platform = new SmokeCapturePlatform(() => sourceWindow);
            platform.SetSourceControlWindow(sourceControlWindow);
            var service = new SelectedTextCaptureService(
                platform,
                automationFallback,
                new SelectedTextCaptureTiming(
                    2,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(1)));
            var result = await service.CaptureAsync(CancellationToken.None);

            if (result.Text != ExpectedSelection
                || result.Error is not null
                || result.ClipboardRestoration != ClipboardRestorationStatus.Restored
                || platform.RestoreCalls != 1)
            {
                throw new InvalidOperationException(
                    $"The complete fallback returned '{result.Text ?? "<null>"}' instead of the selected text. "
                    + $"Error: {result.Error ?? "<none>"}; restoration: {result.ClipboardRestoration}.");
            }

            var descendantPlatform = new SmokeCapturePlatform(() => sourceWindow);
            descendantPlatform.SetSourceControlWindow(sourceWindow);
            var descendantService = new SelectedTextCaptureService(
                descendantPlatform,
                automationFallback,
                new SelectedTextCaptureTiming(
                    2,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(1)));
            var descendantResult = await descendantService.CaptureAsync(
                CancellationToken.None);
            if (descendantResult.Text != ExpectedSelection)
            {
                throw new InvalidOperationException(
                    "UI Automation did not find the selected TextPattern below a source control without that pattern.");
            }

            Console.WriteLine(
                "PASS: the complete UI Automation fallback read the exact selection from direct and descendant live Windows text providers.");
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
                    "The UI Automation smoke provider did not close cleanly.");
            }
        }
    }

    private sealed class SmokeCapturePlatform(Func<IntPtr> getSourceWindow) :
        ISelectedTextCapturePlatform
    {
        private uint _sequenceNumber = 10;
        private IntPtr _sourceControlWindow;

        public int RestoreCalls { get; private set; }

        public IntPtr GetForegroundWindowHandle() => getSourceWindow();

        public IntPtr GetCaretWindowHandle(IntPtr applicationWindowHandle) =>
            _sourceControlWindow;

        public void SetSourceControlWindow(IntPtr sourceControlWindow) =>
            _sourceControlWindow = sourceControlWindow;

        public uint GetClipboardSequenceNumber() => _sequenceNumber;

        public bool TryCaptureSnapshot(out IClipboardCaptureSnapshot? snapshot)
        {
            snapshot = new SmokeSnapshot(this);
            return true;
        }

        public void ClearClipboard() => _sequenceNumber++;

        public string? ReadClipboardText() => null;

        public bool PostCopyMessage(IntPtr windowHandle)
        {
            _sequenceNumber++;
            return true;
        }

        public void RestoreForegroundWindow(IntPtr windowHandle)
        {
        }

        public bool SendCopyShortcut() =>
            throw new InvalidOperationException(
                "The keyboard fallback should not run after a non-text clipboard change.");

        private sealed class SmokeSnapshot(SmokeCapturePlatform owner) :
            IClipboardCaptureSnapshot
        {
            public bool IsComplete => true;

            public void Restore()
            {
                owner.RestoreCalls++;
                owner._sequenceNumber++;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class CopyBlockingTextBox : TextBox
    {
        private const int WmCopy = 0x0301;

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                return true;
            }

            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmCopy)
            {
                return;
            }

            base.WndProc(ref message);
        }
    }

    private static void HidePrivateConsoleWindow()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow == IntPtr.Zero)
        {
            return;
        }

        var consoleProcessIds = new uint[2];
        var consoleProcessCount = GetConsoleProcessList(
            consoleProcessIds,
            (uint)consoleProcessIds.Length);
        if (consoleProcessCount == 1)
        {
            ShowWindow(consoleWindow, SwHide);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processIds,
        uint processCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
