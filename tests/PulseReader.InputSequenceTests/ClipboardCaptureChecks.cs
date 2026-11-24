using PulseReader.Selection;
using System.Runtime.InteropServices;

internal static class ClipboardCaptureChecks
{
    private static readonly SelectedTextCaptureTiming FastTiming = new(
        3,
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(6),
        TimeSpan.FromMilliseconds(8),
        TimeSpan.FromMilliseconds(1));

    public static async Task RunAsync()
    {
        await CheckPostedCopyRestoresSnapshotAsync();
        await CheckFallbackCopyOnlyRunsAfterNoChangeAsync();
        await CheckNonTextCopyDoesNotTriggerDuplicateCopyAsync();
        await CheckAutomationFallbackAfterNonTextCopyAsync();
        await CheckAutomationFallbackWhenClipboardIsBusyAsync();
        await CheckOtherWindowClipboardChangeIsRejectedAsync();
        await CheckAutomationFallbackRequiresOriginalWindowAsync();
        await CheckNewerClipboardContentWinsAsync();
        await CheckPartialSnapshotIsReportedAsync();
        await CheckRestoreFailureIsReportedAsync();
        await CheckChangingSnapshotIsRejectedAsync();
        await CheckChangeBeforeClearIsRejectedAsync();
        await CheckSnapshotCancellationIsContainedAsync();
        await CheckAutomationTimeoutLimitsConcurrentReadersAsync();
        await CheckAutomationProviderFailureIsContainedAsync();

        Console.WriteLine(
            "PASS: clipboard capture preserves newer content, bounds UI Automation fallback, reports partial restoration, contains cancellation, and limits copy fallback.");
    }

    private static async Task CheckPostedCopyRestoresSnapshotAsync()
    {
        var automation = new FakeAutomationFallback("unexpected automation selection");
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = value => value.ReplaceClipboard("  copied selection  ")
        };
        var result = await CaptureAsync(platform, automation);

        Assert(
            result.Text == "copied selection"
            && result.Error is null
            && result.ClipboardRestoration == ClipboardRestorationStatus.Restored
            && result.ClipboardWarning is null
            && platform.RestoreCalls == 1
            && platform.SendCopyCalls == 0
            && automation.Calls == 0
            && platform.CurrentText == FakeCapturePlatform.OriginalText,
            "A direct copy did not restore the complete clipboard snapshot.");
    }

    private static async Task CheckFallbackCopyOnlyRunsAfterNoChangeAsync()
    {
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = _ => { },
            SendCopyAction = value => value.ReplaceClipboard("fallback selection")
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text == "fallback selection"
            && result.ClipboardRestoration == ClipboardRestorationStatus.Restored
            && platform.SendCopyCalls == 1,
            "The keyboard fallback did not run after a posted copy left the clipboard unchanged.");
    }

    private static async Task CheckNonTextCopyDoesNotTriggerDuplicateCopyAsync()
    {
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = value => value.ReplaceClipboard(null),
            SendCopyAction = value => value.ReplaceClipboard("unexpected fallback")
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text is null
            && result.Error is null
            && result.ClipboardRestoration == ClipboardRestorationStatus.Restored
            && platform.SendCopyCalls == 0,
            "A posted copy that changed the clipboard to non-text data triggered a duplicate Ctrl+C operation.");
    }

    private static async Task CheckAutomationFallbackAfterNonTextCopyAsync()
    {
        var automation = new FakeAutomationFallback("automation selection");
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = value => value.ReplaceClipboard(null)
        };
        var result = await CaptureAsync(platform, automation);

        Assert(
            result.Text == "automation selection"
            && result.Error is null
            && result.ClipboardRestoration == ClipboardRestorationStatus.Restored
            && automation.Calls == 1
            && automation.LastSourceWindow == platform.ForegroundWindow
            && automation.LastSourceControlWindow == (IntPtr)202
            && platform.RestoreCalls == 1
            && platform.CurrentText == FakeCapturePlatform.OriginalText,
            "UI Automation did not recover text after the source copied non-text clipboard data.");
    }

    private static async Task CheckAutomationFallbackWhenClipboardIsBusyAsync()
    {
        var automation = new FakeAutomationFallback("automation while clipboard busy");
        var platform = new FakeCapturePlatform
        {
            SnapshotFailuresRemaining = int.MaxValue
        };
        var result = await CaptureAsync(platform, automation);

        Assert(
            result.Text == "automation while clipboard busy"
            && result.Error is null
            && result.ClipboardRestoration == ClipboardRestorationStatus.NotRequired
            && automation.Calls == 1
            && platform.ClearCalls == 0,
            "A busy clipboard prevented the independent UI Automation fallback.");
    }

    private static async Task CheckNewerClipboardContentWinsAsync()
    {
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = value => value.ReplaceClipboard("captured selection"),
            ExternalTextAfterStableRead = "newer clipboard content"
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text == "captured selection"
            && result.ClipboardRestoration == ClipboardRestorationStatus.Superseded
            && result.ClipboardWarning?.Contains("newer contents", StringComparison.Ordinal) == true
            && platform.RestoreCalls == 0
            && platform.CurrentText == "newer clipboard content",
            "Pulse Reader overwrote clipboard content copied after selection capture.");
    }

    private static async Task CheckOtherWindowClipboardChangeIsRejectedAsync()
    {
        var automation = new FakeAutomationFallback("wrong automation selection");
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = value =>
            {
                value.ForegroundWindow = (IntPtr)303;
                value.ReplaceClipboard("text copied in another window");
            }
        };
        var result = await CaptureAsync(platform, automation);

        Assert(
            result.Text is null
            && result.Error?.Contains("another window", StringComparison.Ordinal) == true
            && result.ClipboardRestoration == ClipboardRestorationStatus.Superseded
            && automation.Calls == 0
            && platform.RestoreCalls == 0
            && platform.CurrentText == "text copied in another window",
            "Capture spoke and restored over clipboard text copied from another foreground window.");
    }

    private static async Task CheckAutomationFallbackRequiresOriginalWindowAsync()
    {
        var automation = new FakeAutomationFallback("wrong window selection");
        var platform = new FakeCapturePlatform
        {
            PostCopyAction = _ => { },
            SendCopyAction = value => value.ForegroundWindow = (IntPtr)303
        };
        var result = await CaptureAsync(platform, automation);

        Assert(
            result.Text is null
            && automation.Calls == 0
            && platform.CurrentText == FakeCapturePlatform.OriginalText,
            "UI Automation inspected a window other than the original selection source.");
    }

    private static async Task CheckPartialSnapshotIsReportedAsync()
    {
        var platform = new FakeCapturePlatform
        {
            SnapshotComplete = false,
            PostCopyAction = value => value.ReplaceClipboard("captured selection")
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text == "captured selection"
            && result.ClipboardRestoration == ClipboardRestorationStatus.RestoredPartially
            && result.ClipboardWarning?.Contains(
                "application-specific formats",
                StringComparison.Ordinal) == true,
            "A partial clipboard snapshot was reported as a complete restoration.");
    }

    private static async Task CheckRestoreFailureIsReportedAsync()
    {
        var platform = new FakeCapturePlatform
        {
            RestoreFailuresRemaining = FastTiming.ClipboardRetryCount * 2,
            PostCopyAction = value => value.ReplaceClipboard("captured selection")
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text == "captured selection"
            && result.ClipboardRestoration == ClipboardRestorationStatus.Failed
            && result.ClipboardWarning?.Contains("could not restore", StringComparison.Ordinal) == true,
            "A persistent clipboard restoration failure was not reported.");
    }

    private static async Task CheckChangingSnapshotIsRejectedAsync()
    {
        var platform = new FakeCapturePlatform
        {
            ChangeDuringEverySnapshot = true
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text is null
            && result.Error?.Contains("kept changing", StringComparison.Ordinal) == true
            && platform.ClearCalls == 0
            && platform.RestoreCalls == 0,
            "Capture cleared a clipboard that changed while its snapshot was being materialized.");
    }

    private static async Task CheckChangeBeforeClearIsRejectedAsync()
    {
        var platform = new FakeCapturePlatform
        {
            ChangeBeforeClear = true
        };
        var result = await CaptureAsync(platform);

        Assert(
            result.Text is null
            && result.Error?.Contains("changed before", StringComparison.Ordinal) == true
            && result.ClipboardRestoration == ClipboardRestorationStatus.NotRequired
            && platform.ClearCalls == 0
            && platform.RestoreCalls == 0,
            "Capture cleared or restored over content copied after the snapshot completed.");
    }

    private static async Task CheckSnapshotCancellationIsContainedAsync()
    {
        var timing = FastTiming with
        {
            ClipboardRetryCount = 10,
            ClipboardRetryDelay = TimeSpan.FromMilliseconds(50)
        };
        var platform = new FakeCapturePlatform
        {
            SnapshotFailuresRemaining = int.MaxValue
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        var service = new SelectedTextCaptureService(
            platform,
            new FakeAutomationFallback(null),
            timing);
        var result = await service.CaptureAsync(cancellation.Token);

        Assert(
            result.Error == "Selection capture was cancelled."
            && result.ClipboardRestoration == ClipboardRestorationStatus.NotRequired
            && platform.ClearCalls == 0,
            "Cancellation during initial clipboard snapshot retries escaped the capture task.");
    }

    private static async Task CheckAutomationTimeoutLimitsConcurrentReadersAsync()
    {
        using var releaseFirstRead = new ManualResetEventSlim();
        using var firstReadCompleted = new ManualResetEventSlim();
        var reader = new FakeUiAutomationSelectionReader(callNumber =>
        {
            if (callNumber == 1)
            {
                try
                {
                    releaseFirstRead.Wait();
                }
                finally
                {
                    firstReadCompleted.Set();
                }

                return "late selection";
            }

            return "next selection";
        });

        var fallback = new UiAutomationSelectedTextFallback(
            reader,
            TimeSpan.FromMilliseconds(10));
        var target = new SelectedTextAutomationTarget((IntPtr)101, (IntPtr)202);
        var firstResult = await fallback.TryCaptureAsync(target, CancellationToken.None);
        var overlappingResult = await fallback.TryCaptureAsync(target, CancellationToken.None);

        releaseFirstRead.Set();
        Assert(
            firstReadCompleted.Wait(TimeSpan.FromSeconds(1)),
            "The timed-out UI Automation reader did not complete after release.");
        await Task.Delay(20);

        var nextResult = await fallback.TryCaptureAsync(target, CancellationToken.None);
        Assert(
            firstResult is null
            && overlappingResult is null
            && nextResult == "next selection"
            && reader.Calls == 2,
            "A timed-out UI Automation request allowed overlapping readers or remained permanently stuck.");
    }

    private static async Task CheckAutomationProviderFailureIsContainedAsync()
    {
        var reader = new FakeUiAutomationSelectionReader(
            _ => throw new InvalidOperationException("Simulated provider failure."));
        var fallback = new UiAutomationSelectedTextFallback(
            reader,
            TimeSpan.FromMilliseconds(50));

        var result = await fallback.TryCaptureAsync(
            new SelectedTextAutomationTarget((IntPtr)101, (IntPtr)202),
            CancellationToken.None);
        Assert(
            result is null && reader.Calls == 1,
            "A UI Automation provider failure escaped the optional fallback.");
    }

    private static Task<SelectedTextCaptureResult> CaptureAsync(
        FakeCapturePlatform platform,
        ISelectedTextAutomationFallback? automationFallback = null) =>
        new SelectedTextCaptureService(
            platform,
            automationFallback ?? new FakeAutomationFallback(null),
            FastTiming).CaptureAsync(CancellationToken.None);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeAutomationFallback(string? text) : ISelectedTextAutomationFallback
    {
        public int Calls { get; private set; }

        public IntPtr LastSourceWindow { get; private set; }

        public IntPtr LastSourceControlWindow { get; private set; }

        public Task<string?> TryCaptureAsync(
            SelectedTextAutomationTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastSourceWindow = target.SourceWindow;
            LastSourceControlWindow = target.SourceControlWindow;
            return Task.FromResult(text);
        }
    }

    private sealed class FakeUiAutomationSelectionReader(Func<int, string?> read) :
        IUiAutomationSelectionReader
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? ReadSelectedText(SelectedTextAutomationTarget target)
        {
            var callNumber = Interlocked.Increment(ref _calls);
            return read(callNumber);
        }
    }

    private sealed class FakeCapturePlatform : ISelectedTextCapturePlatform
    {
        public const string OriginalText = "original clipboard";

        private uint _sequenceNumber = 10;
        private int _stableReadsBeforeExternalChange = -1;

        public bool SnapshotComplete { get; init; } = true;

        public bool ChangeDuringEverySnapshot { get; init; }

        public bool ChangeBeforeClear { get; init; }

        public int SnapshotFailuresRemaining { get; set; }

        public int RestoreFailuresRemaining { get; set; }

        public string? ExternalTextAfterStableRead { get; init; }

        public Action<FakeCapturePlatform>? PostCopyAction { get; init; }

        public Action<FakeCapturePlatform>? SendCopyAction { get; init; }

        public string? CurrentText { get; private set; } = OriginalText;

        public int ClearCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int SendCopyCalls { get; private set; }

        public IntPtr ForegroundWindow { get; set; } = (IntPtr)101;

        public IntPtr GetForegroundWindowHandle() => ForegroundWindow;

        public IntPtr GetCaretWindowHandle(IntPtr applicationWindowHandle) => (IntPtr)202;

        public uint GetClipboardSequenceNumber()
        {
            if (_stableReadsBeforeExternalChange == 0)
            {
                _stableReadsBeforeExternalChange = -1;
                ReplaceClipboard(ExternalTextAfterStableRead);
            }
            else if (_stableReadsBeforeExternalChange > 0)
            {
                _stableReadsBeforeExternalChange--;
            }

            return _sequenceNumber;
        }

        public bool TryCaptureSnapshot(out IClipboardCaptureSnapshot? snapshot)
        {
            if (SnapshotFailuresRemaining > 0)
            {
                SnapshotFailuresRemaining--;
                throw new ExternalException("Simulated busy clipboard.");
            }

            snapshot = new FakeSnapshot(this, SnapshotComplete);
            if (ChangeDuringEverySnapshot)
            {
                ReplaceClipboard("changing clipboard");
            }
            else if (ChangeBeforeClear)
            {
                _stableReadsBeforeExternalChange = 1;
            }

            return true;
        }

        public void ClearClipboard()
        {
            ClearCalls++;
            ReplaceClipboard(null);
        }

        public string? ReadClipboardText()
        {
            if (ExternalTextAfterStableRead is not null)
            {
                _stableReadsBeforeExternalChange = 1;
            }

            return CurrentText;
        }

        public bool PostCopyMessage(IntPtr windowHandle)
        {
            PostCopyAction?.Invoke(this);
            return PostCopyAction is not null;
        }

        public void RestoreForegroundWindow(IntPtr windowHandle)
        {
        }

        public bool SendCopyShortcut()
        {
            SendCopyCalls++;
            SendCopyAction?.Invoke(this);
            return SendCopyAction is not null;
        }

        public void ReplaceClipboard(string? text)
        {
            CurrentText = text;
            _sequenceNumber++;
        }

        private sealed class FakeSnapshot(
            FakeCapturePlatform owner,
            bool isComplete) : IClipboardCaptureSnapshot
        {
            public bool IsComplete { get; } = isComplete;

            public void Restore()
            {
                owner.RestoreCalls++;
                if (owner.RestoreFailuresRemaining > 0)
                {
                    owner.RestoreFailuresRemaining--;
                    throw new ExternalException("Simulated restore failure.");
                }

                owner.ReplaceClipboard(OriginalText);
            }

            public void Dispose()
            {
            }
        }
    }
}
