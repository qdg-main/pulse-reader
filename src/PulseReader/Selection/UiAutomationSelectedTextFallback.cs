using System.Runtime.InteropServices;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Selection;

internal interface ISelectedTextAutomationFallback
{
    Task<string?> TryCaptureAsync(
        SelectedTextAutomationTarget target,
        CancellationToken cancellationToken);
}

internal interface IUiAutomationSelectionReader
{
    string? ReadSelectedText(SelectedTextAutomationTarget target);
}

internal readonly record struct SelectedTextAutomationTarget(
    IntPtr SourceWindow,
    IntPtr SourceControlWindow);

internal sealed class UiAutomationSelectedTextFallback : ISelectedTextAutomationFallback
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Lock _sync = new();
    private readonly IUiAutomationSelectionReader _reader;
    private readonly TimeSpan _timeout;
    private Task<string?>? _activeRead;

    public UiAutomationSelectedTextFallback()
        : this(new WindowsUiAutomationSelectionReader(), DefaultTimeout)
    {
    }

    internal UiAutomationSelectedTextFallback(
        IUiAutomationSelectionReader reader,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _reader = reader;
        _timeout = timeout;
    }

    public async Task<string?> TryCaptureAsync(
        SelectedTextAutomationTarget target,
        CancellationToken cancellationToken)
    {
        if (target.SourceWindow == IntPtr.Zero)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        Task<string?> readTask;
        lock (_sync)
        {
            if (_activeRead is { IsCompleted: false })
            {
                return null;
            }

            readTask = Task.Run(() => ReadSafely(target), CancellationToken.None);
            _activeRead = readTask;
        }

        try
        {
            return await readTask.WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (readTask.IsCompleted)
            {
                ClearCompletedRead(readTask);
            }
            else
            {
                _ = ClearWhenCompletedAsync(readTask);
            }
        }
    }

    private string? ReadSafely(SelectedTextAutomationTarget target)
    {
        try
        {
            var text = _reader.ReadSelectedText(target)?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (IsUiAutomationException(exception))
        {
            return null;
        }
    }

    private async Task ClearWhenCompletedAsync(Task<string?> readTask)
    {
        try
        {
            await readTask;
        }
        catch (Exception)
        {
            // ReadSafely normally contains provider failures. Observe any exceptional completion.
        }
        finally
        {
            ClearCompletedRead(readTask);
        }
    }

    private void ClearCompletedRead(Task<string?> readTask)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeRead, readTask))
            {
                _activeRead = null;
            }
        }
    }

    private static bool IsUiAutomationException(Exception exception) =>
        exception is ArgumentException
            or ExternalException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException;
}

internal sealed class WindowsUiAutomationSelectionReader : IUiAutomationSelectionReader
{
    private const int MaximumAncestorDepth = 32;
    private readonly Func<IntPtr> _getForegroundWindow;

    public WindowsUiAutomationSelectionReader()
        : this(NativeInput.GetForegroundWindowHandle)
    {
    }

    internal WindowsUiAutomationSelectionReader(Func<IntPtr> getForegroundWindow)
    {
        ArgumentNullException.ThrowIfNull(getForegroundWindow);
        _getForegroundWindow = getForegroundWindow;
    }

    public string? ReadSelectedText(SelectedTextAutomationTarget target)
    {
        var sourceWindow = target.SourceWindow;
        if (_getForegroundWindow() != sourceWindow)
        {
            return null;
        }

        UIA.IUIAutomation automation;
        try
        {
            automation = new UIA.CUIAutomation8();
        }
        catch (COMException)
        {
            automation = new UIA.CUIAutomation();
        }

        var sourceElement = automation.ElementFromHandle(sourceWindow);
        if (sourceElement is null)
        {
            return null;
        }

        var sourceProcessId = sourceElement.CurrentProcessId;
        if (target.SourceControlWindow != IntPtr.Zero)
        {
            var sourceControl = automation.ElementFromHandle(target.SourceControlWindow);
            if (sourceControl is not null
                && sourceControl.CurrentProcessId == sourceProcessId)
            {
                var controlText = TryReadSelection(sourceControl)
                    ?? TryReadSelectionFromDescendants(
                        automation,
                        sourceControl,
                        requireKeyboardFocus: false);
                if (controlText is not null)
                {
                    return IsSourceStillForeground(sourceWindow) ? controlText : null;
                }
            }
        }

        var focusedElement = automation.GetFocusedElement();
        if (focusedElement is not null
            && focusedElement.CurrentProcessId == sourceProcessId)
        {
            var current = focusedElement;
            for (var depth = 0; depth < MaximumAncestorDepth && current is not null; depth++)
            {
                var text = TryReadSelection(current);
                if (text is not null)
                {
                    return IsSourceStillForeground(sourceWindow) ? text : null;
                }

                current = automation.ControlViewWalker.GetParentElement(current);
            }
        }

        var sourceText = TryReadSelection(sourceElement);
        if (sourceText is not null)
        {
            return IsSourceStillForeground(sourceWindow) ? sourceText : null;
        }

        var focusedDescendantText = TryReadSelectionFromDescendants(
            automation,
            sourceElement,
            requireKeyboardFocus: true);
        return focusedDescendantText is not null
               && IsSourceStillForeground(sourceWindow)
            ? focusedDescendantText
            : null;
    }

    private static string? TryReadSelectionFromDescendants(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement root,
        bool requireKeyboardFocus)
    {
        var textPatternCondition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId,
            true);
        var condition = requireKeyboardFocus
            ? automation.CreateAndCondition(
                textPatternCondition,
                automation.CreatePropertyCondition(
                    UIA.UIA_PropertyIds.UIA_HasKeyboardFocusPropertyId,
                    true))
            : textPatternCondition;
        var textProviders = root.FindAll(
            UIA.TreeScope.TreeScope_Descendants,
            condition);
        if (textProviders is null)
        {
            return null;
        }

        for (var index = 0; index < textProviders.Length; index++)
        {
            var textProvider = textProviders.GetElement(index);
            var selectedText = textProvider is null
                ? null
                : TryReadSelection(textProvider);
            if (selectedText is not null)
            {
                return selectedText;
            }
        }

        return null;
    }

    private bool IsSourceStillForeground(IntPtr sourceWindow) =>
        _getForegroundWindow() == sourceWindow;

    private static string? TryReadSelection(UIA.IUIAutomationElement element)
    {
        if (element.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId)
                is not UIA.IUIAutomationTextPattern textPattern
            || textPattern.SupportedTextSelection
                == UIA.SupportedTextSelection.SupportedTextSelection_None)
        {
            return null;
        }

        var selectedRanges = textPattern.GetSelection();
        if (selectedRanges is null || selectedRanges.Length == 0)
        {
            return null;
        }

        var selectedText = new List<string>();
        for (var index = 0; index < selectedRanges.Length; index++)
        {
            var selectedRange = selectedRanges.GetElement(index);
            var text = selectedRange?.GetText(-1)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                selectedText.Add(text);
            }
        }

        var combinedText = string.Join(Environment.NewLine, selectedText);
        return string.IsNullOrWhiteSpace(combinedText) ? null : combinedText;
    }
}
